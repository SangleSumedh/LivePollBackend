using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using live_poll_backend.Data;
using live_poll_backend.Hubs;
using live_poll_backend.Models.Entities;
using live_poll_backend.Models.Enums;

namespace live_poll_backend.Services;

public class SavePayload
{
    public string PollId { get; }
    public int QuestionIndex { get; }
    public Dictionary<string, int> Snapshot { get; }

    public SavePayload(string pollId, int questionIndex, Dictionary<string, int> snapshot)
    {
        PollId = pollId;
        QuestionIndex = questionIndex;
        Snapshot = snapshot;
    }
}

public class WordCloudManager : IHostedService, IDisposable
{
    private readonly IServiceProvider _serviceProvider;
    private readonly IHubContext<PollHub> _hubContext;
    private readonly ILogger<WordCloudManager> _logger;

    // In-memory cache of current word counts: (PollId, QuestionIndex) -> (Word -> Count)
    private readonly ConcurrentDictionary<(string PollId, int QIdx), ConcurrentDictionary<string, int>> _counts = new();

    // Dirty flags to track updated poll/questions since the last tick
    private readonly ConcurrentDictionary<(string PollId, int QIdx), byte> _dirty = new();

    // Bounded channel to decouple tick broadcasts from slow database flushes
    private readonly Channel<SavePayload> _saveChannel;

    private readonly HashSet<string> _stopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "the", "a", "an", "and", "or", "is", "it", "in", "of", "to", "for", "on", "at", "with", "this", "that", "was", "be", "are", 
        "i", "you", "we", "they", "he", "she", "not", "but", "by", "from", "as", "have", "has", "had", "do", "did", "been", "will", 
        "my", "your", "our", "can", "so", "if", "up", "out", "about", "who", "get", "which", "go", "me", "when", "make", "can", "like", 
        "time", "just", "him", "know", "take", "people", "into", "year", "your", "good", "some", "could", "them", "see", "other", 
        "than", "then", "now", "look", "only", "come", "its", "over", "think", "also", "back", "after", "use", "two", "how", "our", 
        "work", "first", "well", "way", "even", "new", "want", "because", "any", "these", "give", "day", "most", "us"
    };

    private CancellationTokenSource? _cts;
    private Task? _tickTask;
    private Task? _saveConsumerTask;

    public WordCloudManager(
        IServiceProvider serviceProvider,
        IHubContext<PollHub> hubContext,
        ILogger<WordCloudManager> logger)
    {
        _serviceProvider = serviceProvider;
        _hubContext = hubContext;
        _logger = logger;
        _saveChannel = Channel.CreateBounded<SavePayload>(new BoundedChannelOptions(1000)
        {
            SingleWriter = true,
            SingleReader = true,
            FullMode = BoundedChannelFullMode.DropWrite // Fallback strategy: drop writes under severe queue saturation
        });
    }

    /// <summary>
    /// Cold-start rehydration: load word cloud counts for active polls on startup.
    /// </summary>
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("WordCloudManager is starting and rehydrating active word clouds...");
        _cts = new CancellationTokenSource();

        try
        {
            using var scope = _serviceProvider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            // Find all polls that are currently 'Live'
            var livePolls = await db.Polls
                .Where(p => p.Status == PollStatus.Live)
                .Select(p => p.Id)
                .ToListAsync(cancellationToken);

            foreach (var pollId in livePolls)
            {
                // Query all saved word counts for this poll's word cloud questions
                var countsInDb = await db.WordCloudCounts
                    .Where(w => w.PollId == pollId)
                    .ToListAsync(cancellationToken);

                // Group by QuestionIndex and load into memory cache
                var grouped = countsInDb.GroupBy(w => w.QuestionIndex);
                foreach (var group in grouped)
                {
                    var qIdx = group.Key;
                    var dict = new ConcurrentDictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                    foreach (var item in group)
                    {
                        dict[item.Word] = item.Count;
                    }
                    _counts[(pollId, qIdx)] = dict;
                }
            }

            _logger.LogInformation("WordCloudManager rehydration complete. Loaded {Count} active word clouds.", _counts.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error occurred during WordCloudManager cold-start rehydration.");
        }

        // Start background tasks
        _tickTask = RunTickLoopAsync(_cts.Token);
        _saveConsumerTask = RunSaveConsumerAsync(_cts.Token);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("WordCloudManager is stopping...");
        if (_cts != null)
        {
            _cts.Cancel();
        }

        // Wait for tasks to complete
        var tasks = new List<Task>();
        if (_tickTask != null) tasks.Add(_tickTask);
        if (_saveConsumerTask != null) tasks.Add(_saveConsumerTask);

        await Task.WhenAll(tasks);
        _logger.LogInformation("WordCloudManager background tasks stopped.");
    }

    /// <summary>
    /// Processes and records text submissions.
    /// </summary>
    public void RecordSubmission(string pollId, int questionIndex, string text)
    {
        var sanitized = SanitizeText(text);
        if (sanitized.Count == 0) return;

        var key = (pollId, questionIndex);
        var questionDict = _counts.GetOrAdd(key, _ => new ConcurrentDictionary<string, int>(StringComparer.OrdinalIgnoreCase));

        // Deduplicate words within a single response submission so each word receives max 1 vote per submitter
        var uniqueWords = new HashSet<string>(sanitized, StringComparer.OrdinalIgnoreCase);

        foreach (var word in uniqueWords)
        {
            questionDict.AddOrUpdate(word, 1, (_, current) => current + 1);
        }

        // Mark as dirty
        _dirty[key] = 0;
    }

    /// <summary>
    /// Loads a snapshot of word cloud counts from DB into memory (first-write-wins guard).
    /// </summary>
    public void LoadFromDb(string pollId, int questionIndex, Dictionary<string, int> dbCounts)
    {
        var key = (pollId, questionIndex);
        // Guard: if key already exists, return without overwriting fresh in-memory counts
        if (_counts.ContainsKey(key)) return;

        var dict = new ConcurrentDictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var kvp in dbCounts)
        {
            dict[kvp.Key] = kvp.Value;
        }

        _counts.TryAdd(key, dict);
    }

    /// <summary>
    /// Removes cache entries atomically when a poll is restarted to avoid race conditions.
    /// </summary>
    public void InvalidateKey(string pollId, int questionIndex)
    {
        var key = (pollId, questionIndex);
        _counts.TryRemove(key, out _);
        _dirty.TryRemove(key, out _);
    }

    /// <summary>
    /// Returns the top 50 words by frequency, including count >= 1.
    /// </summary>
    public Dictionary<string, int> GetTop50(string pollId, int questionIndex)
    {
        var key = (pollId, questionIndex);
        if (!_counts.TryGetValue(key, out var dict))
        {
            return new Dictionary<string, int>();
        }

        return dict
            .Where(kvp => kvp.Value >= 1)
            .OrderByDescending(kvp => kvp.Value)
            .Take(50)
            .ToDictionary(kvp => kvp.Key, kvp => kvp.Value, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Sanitizes raw user input: converts to lowercase, removes punctuation, filters out stop words.
    /// </summary>
    public List<string> SanitizeText(string input)
    {
        if (string.IsNullOrWhiteSpace(input)) return new List<string>();

        var lowercase = input.ToLowerInvariant();

        // Replace punctuation with spaces to avoid merging words (e.g. "hello,world" -> "hello world")
        var sb = new StringBuilder();
        foreach (var c in lowercase)
        {
            if (char.IsPunctuation(c))
            {
                sb.Append(' ');
            }
            else
            {
                sb.Append(c);
            }
        }

        var tokens = sb.ToString().Split(new[] { ' ', '\r', '\n', '\t' }, StringSplitOptions.RemoveEmptyEntries);
        var result = new List<string>();

        foreach (var token in tokens)
        {
            var word = token.Trim();
            if (word.Length > 0 && !_stopWords.Contains(word))
            {
                result.Add(word);
            }
        }

        return result;
    }

    /// <summary>
    /// Every 0.25 seconds, checks dirty keys, broadcasts top 50 to SignalR clients, and queue DB saves.
    /// </summary>
    private async Task RunTickLoopAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(250));
        while (await timer.WaitForNextTickAsync(ct))
        {
            try
            {
                var dirtyKeys = _dirty.Keys.ToArray();
                foreach (var key in dirtyKeys)
                {
                    // INTENTIONAL DRAIN-BEFORE-BROADCAST INVARIANT:
                    // TryRemove here ensures any votes landing while we process this tick
                    // will re-mark the key as dirty and get handled in the next loop iteration.
                    _dirty.TryRemove(key, out _);

                    var top50 = GetTop50(key.PollId, key.QIdx);

                    // Broadcast to SignalR client group
                    await _hubContext.Clients.Group($"poll_{key.PollId}")
                        .SendAsync("WordCloudUpdated", new
                        {
                            pollId = key.PollId,
                            questionIndex = key.QIdx,
                            words = top50
                        }, cancellationToken: ct);

                    // Enqueue save payload into channel (non-blocking)
                    var fullSnapshot = GetFullSnapshot(key.PollId, key.QIdx);
                    if (fullSnapshot.Count > 0)
                    {
                        var payload = new SavePayload(key.PollId, key.QIdx, fullSnapshot);
                        _saveChannel.Writer.TryWrite(payload);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Exception occurred during WordCloudManager tick broadcast loop.");
            }
        }
    }

    /// <summary>
    /// Reads from the bounded channel and flushes updates to PostgreSQL in the background.
    /// </summary>
    private async Task RunSaveConsumerAsync(CancellationToken ct)
    {
        var reader = _saveChannel.Reader;
        while (await reader.WaitToReadAsync(ct))
        {
            while (reader.TryRead(out var payload))
            {
                try
                {
                    using var scope = _serviceProvider.CreateScope();
                    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

                    // Load existing word cloud entries for this specific question
                    var existingCounts = await db.WordCloudCounts
                        .Where(w => w.PollId == payload.PollId && w.QuestionIndex == payload.QuestionIndex)
                        .ToDictionaryAsync(w => w.Word, StringComparer.OrdinalIgnoreCase, ct);

                    foreach (var kvp in payload.Snapshot)
                    {
                        var word = kvp.Key;
                        var count = kvp.Value;

                        if (existingCounts.TryGetValue(word, out var entity))
                        {
                            entity.Count = count;
                        }
                        else
                        {
                            db.WordCloudCounts.Add(new WordCloudCount
                            {
                                PollId = payload.PollId,
                                QuestionIndex = payload.QuestionIndex,
                                Word = word,
                                Count = count
                            });
                        }
                    }

                    await db.SaveChangesAsync(ct);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "WordCloud DB flush failed for poll {PollId} QIndex {QIndex}. Changes will remain in memory.",
                        payload.PollId, payload.QuestionIndex);
                }
            }
        }
    }

    private Dictionary<string, int> GetFullSnapshot(string pollId, int questionIndex)
    {
        var key = (pollId, questionIndex);
        if (!_counts.TryGetValue(key, out var dict))
        {
            return new Dictionary<string, int>();
        }
        return dict.ToDictionary(kvp => kvp.Key, kvp => kvp.Value, StringComparer.OrdinalIgnoreCase);
    }

    public void Dispose()
    {
        _cts?.Dispose();
    }
}
