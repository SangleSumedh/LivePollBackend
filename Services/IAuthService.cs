using live_poll_backend.Models.DTOs.Auth;

namespace live_poll_backend.Services;

public interface IAuthService
{
    Task<AuthResponse> RegisterAsync(RegisterRequest request);
    Task<AuthResponse> LoginAsync(LoginRequest request);
}
