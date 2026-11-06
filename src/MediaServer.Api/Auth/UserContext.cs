using MediaServer.Api.Endpoints;

namespace MediaServer.Api.Auth;

/// <summary>Who is asking. Services that keep per-person data (watch history, My List) use this instead of taking a user everywhere.</summary>
public interface IUserContext
{
    long UserId { get; }
}

/// <summary>The signed-in person for the current request. With sign-in off (or outside a request) everything belongs to user 1.</summary>
public sealed class HttpUserContext(IHttpContextAccessor http) : IUserContext
{
    public long UserId => http.HttpContext?.Items[AuthEndpoints.UserItem] is AuthUser u ? u.Id : 1;
}
