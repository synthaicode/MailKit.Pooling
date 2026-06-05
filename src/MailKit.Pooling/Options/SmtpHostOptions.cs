namespace MailKit.Pooling.Options;

public sealed class SmtpHostOptions
{
    public string Host { get; set; } = string.Empty;
    public int Port { get; set; } = 587;
    public string? UserName { get; set; }
    public string? Password { get; set; }
    public string SecureSocketOptions { get; set; } = "StartTlsWhenAvailable";
}
