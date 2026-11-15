namespace ShutterSpool.Application.Security;

public sealed record AccountEnrollmentIdentity(string Issuer, string Subject, string Email);
public sealed record AccountEnrollmentResult(string Status, AccountEnrollmentIdentity? Account = null);

public interface IAccountEnrollment
{
    Task<AccountEnrollmentResult> ResolveAsync(string email, CancellationToken token);
    Task<string> SendSetupEmailAsync(AccountEnrollmentIdentity account, CancellationToken token);
}
