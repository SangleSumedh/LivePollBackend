namespace live_poll_backend.Models.DTOs.Auth;

public class AuthResponse
{
    public string Token { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string UserId { get; set; } = string.Empty;
}
