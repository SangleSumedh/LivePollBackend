import { create } from "zustand";
import { db } from "@/lib/firebase";
import {
  collection,
  doc,
  getDoc,
  getDocs,
  setDoc,
  updateDoc,
  deleteDoc,
  onSnapshot,
  runTransaction,
  writeBatch,
  query,
  where,
  serverTimestamp,
  increment
} from "firebase/firestore";

const generatePollId = () =>
  Math.random().toString(36).substring(2, 8).toUpperCase();

export const usePollStore = create((set, get) => ({
  polls: [],
  currentPoll: null,
  loading: false,
  loadingCurrent: false,
  error: null,
  isSaving: false,

  // Fetch all polls created by a user
  fetchPolls: async (userId) => {
    if (!userId) return;
    set({ loading: true, error: null });
    try {
      const q = query(collection(db, "polls"), where("createdBy", "==", userId));
      const snap = await getDocs(q);
      const data = snap.docs.map((d) => ({
        id: d.id,
        ...d.data(),
        createdAt: d.data().createdAt?.toDate() || new Date(),
      }));
      data.sort((a, b) => b.createdAt - a.createdAt);
      set({ polls: data, loading: false });
    } catch (err) {
      console.error("Error fetching polls:", err);
      set({ error: "Failed to load polls", loading: false });
    }
  },

  // Fetch single poll by ID
  fetchPollById: async (pollId) => {
    if (!pollId) return null;
    set({ loadingCurrent: true, error: null });
    try {
      const pollDoc = await getDoc(doc(db, "polls", pollId));
      if (pollDoc.exists()) {
        const data = { id: pollDoc.id, ...pollDoc.data() };
        set({ currentPoll: data, loadingCurrent: false });
        return data;
      } else {
        set({ error: "Poll not found", loadingCurrent: false });
        return null;
      }
    } catch (err) {
      console.error("Error fetching poll by ID:", err);
      set({ error: "Failed to fetch poll detail", loadingCurrent: false });
      return null;
    }
  },

  // Subscribe to real-time updates for a single poll
  subscribeToPoll: (pollId) => {
    if (!pollId) return () => {};
    set({ loadingCurrent: true, error: null });

    const unsubscribe = onSnapshot(
      doc(db, "polls", pollId),
      (docSnap) => {
        if (docSnap.exists()) {
          set({
            currentPoll: { id: docSnap.id, ...docSnap.data() },
            loadingCurrent: false,
            error: null
          });
        } else {
          set({ error: "Poll not found", loadingCurrent: false, currentPoll: null });
        }
      },
      (error) => {
        console.error("Error listening to poll:", error);
        set({ error: "Failed to sync poll updates", loadingCurrent: false });
      }
    );

    return unsubscribe;
  },

  // Create a new poll
  createPoll: async (title, questions, userId, userEmail, userName) => {
    set({ isSaving: true });
    try {
      const pollId = generatePollId();
      const questionsData = questions.map((q) => ({
        text: q.text.trim(),
        options: q.options
          .filter((opt) => opt.trim() !== "")
          .map((o) => ({ text: o.trim() })),
      }));

      const voteCounts = {};
      questionsData.forEach((q, qIdx) => {
        q.options.forEach((_, optIdx) => {
          voteCounts[`${qIdx}_${optIdx}`] = 0;
        });
      });

      await setDoc(doc(db, "polls", pollId), {
        title: title.trim(),
        createdBy: userId,
        createdByEmail: userEmail,
        createdByName: userName || "Anonymous",
        status: "draft",
        activeQuestionIndex: -1,
        currentQuestionActive: false,
        questions: questionsData,
        voteCounts,
        createdAt: serverTimestamp(),
        updatedAt: serverTimestamp(),
      });

      set({ isSaving: false });
      return pollId;
    } catch (err) {
      console.error("Error creating poll:", err);
      set({ isSaving: false });
      throw err;
    }
  },

  // Save changes to an existing poll's title and questions
  savePoll: async (pollId, title, questions) => {
    set({ isSaving: true });
    try {
      const questionsData = questions.map((q) => ({
        text: q.text.trim(),
        options: q.options
          .filter((opt) => opt.trim() !== "")
          .map((o) => ({ text: typeof o === "string" ? o.trim() : (o.text || "").trim() })),
      }));

      const voteCounts = {};
      questionsData.forEach((q, qIdx) => {
        q.options.forEach((_, optIdx) => {
          voteCounts[`${qIdx}_${optIdx}`] = 0;
        });
      });

      await updateDoc(doc(db, "polls", pollId), {
        title: title.trim(),
        questions: questionsData,
        voteCounts,
        updatedAt: serverTimestamp(),
      });

      set({ isSaving: false });
    } catch (err) {
      console.error("Error saving poll:", err);
      set({ isSaving: false });
      throw err;
    }
  },

  // Delete poll and all associated votes
  deletePoll: async (pollId) => {
    try {
      const votesSnap = await getDocs(collection(db, "polls", pollId, "votes"));
      const batch = writeBatch(db);
      votesSnap.forEach((v) => batch.delete(v.ref));
      await batch.commit();
      
      await deleteDoc(doc(db, "polls", pollId));
      
      // Update local state
      set((state) => ({
        polls: state.polls.filter((p) => p.id !== pollId),
        currentPoll: state.currentPoll?.id === pollId ? null : state.currentPoll
      }));
    } catch (err) {
      console.error("Error deleting poll:", err);
      throw err;
    }
  },

  // Restart poll: clears votes sub-collection and resets parameters
  restartPoll: async (pollId, poll) => {
    try {
      const votesSnap = await getDocs(collection(db, "polls", pollId, "votes"));
      const batch = writeBatch(db);
      votesSnap.forEach((v) => batch.delete(v.ref));

      const resetCounts = {};
      poll.questions?.forEach((q, qIdx) => {
        q.options.forEach((_, optIdx) => {
          resetCounts[`${qIdx}_${optIdx}`] = 0;
        });
      });

      batch.update(doc(db, "polls", pollId), {
        status: "draft",
        activeQuestionIndex: -1,
        currentQuestionActive: false,
        voteCounts: resetCounts,
      });

      await batch.commit();
    } catch (err) {
      console.error("Error restarting poll:", err);
      throw err;
    }
  },

  // Presenter actions: start voting
  startVoting: async (pollId, activeQuestionIndex) => {
    try {
      await updateDoc(doc(db, "polls", pollId), {
        status: "live",
        activeQuestionIndex: activeQuestionIndex >= 0 ? activeQuestionIndex : 0,
        currentQuestionActive: true,
      });
    } catch (err) {
      console.error("Error starting voting:", err);
      throw err;
    }
  },

  // Presenter actions: stop voting
  stopVoting: async (pollId) => {
    try {
      await updateDoc(doc(db, "polls", pollId), {
        currentQuestionActive: false,
      });
    } catch (err) {
      console.error("Error stopping voting:", err);
      throw err;
    }
  },

  // Presenter actions: next question
  nextQuestion: async (pollId, activeQuestionIndex) => {
    try {
      await updateDoc(doc(db, "polls", pollId), {
        activeQuestionIndex: activeQuestionIndex + 1,
        currentQuestionActive: false,
      });
    } catch (err) {
      console.error("Error going to next question:", err);
      throw err;
    }
  },

  // Presenter actions: previous question
  prevQuestion: async (pollId, activeQuestionIndex) => {
    try {
      await updateDoc(doc(db, "polls", pollId), {
        activeQuestionIndex: activeQuestionIndex - 1,
        currentQuestionActive: false,
      });
    } catch (err) {
      console.error("Error going to previous question:", err);
      throw err;
    }
  },

  // Presenter actions: end poll
  endPoll: async (pollId) => {
    try {
      await updateDoc(doc(db, "polls", pollId), {
        status: "ended",
        currentQuestionActive: false,
      });
    } catch (err) {
      console.error("Error ending poll:", err);
      throw err;
    }
  },

  // Check if user already voted on current question
  checkVoteStatus: async (pollId, activeQuestionIndex, sessionId) => {
    if (!pollId || activeQuestionIndex === undefined || activeQuestionIndex < 0 || !sessionId) return null;
    try {
      const voteRef = doc(db, "polls", pollId, "votes", `${sessionId}_${activeQuestionIndex}`);
      const voteDoc = await getDoc(voteRef);
      if (voteDoc.exists()) {
        return voteDoc.data().optionIndex;
      }
      return null;
    } catch (err) {
      console.error("Error checking vote status:", err);
      return null;
    }
  },

  // Vote for option (Transactional method used in JSX participant view)
  voteForOption: async (pollId, activeQuestionIndex, optionIndex, sessionId) => {
    const voteRef = doc(db, "polls", pollId, "votes", `${sessionId}_${activeQuestionIndex}`);
    const pollRef = doc(db, "polls", pollId);

    try {
      await runTransaction(db, async (transaction) => {
        const voteDoc = await transaction.get(voteRef);
        if (voteDoc.exists()) {
          throw new Error("You have already voted on this question");
        }

        transaction.set(voteRef, {
          sessionId,
          questionIndex: activeQuestionIndex,
          optionIndex,
          timestamp: serverTimestamp(),
        });

        transaction.update(pollRef, {
          [`voteCounts.${activeQuestionIndex}_${optionIndex}`]: increment(1),
        });
      });
    } catch (err) {
      console.error("Error voting:", err);
      throw err;
    }
  },

  // Legacy Vote (Direct questions array update used in .js participant view)
  voteForOptionLegacy: async (pollId, activeQuestionIndex, optionIndex, questions) => {
    try {
      const pollRef = doc(db, "polls", pollId);
      const currentQuestion = questions[activeQuestionIndex];
      
      const updatedOptions = [...currentQuestion.options];
      updatedOptions[optionIndex] = {
        ...updatedOptions[optionIndex],
        votes: (updatedOptions[optionIndex].votes || 0) + 1
      };

      const updatedQuestions = [...questions];
      updatedQuestions[activeQuestionIndex] = {
        ...currentQuestion,
        options: updatedOptions
      };

      await updateDoc(pollRef, {
        questions: updatedQuestions
      });
    } catch (err) {
      console.error("Error voting (legacy):", err);
      throw err;
    }
  }
}));
