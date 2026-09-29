namespace NexMud.Jev.Typesafe;

public sealed record TypesafeJevOptions(
    string ApiKey,
    string Model = "jev-latest",
    Uri? BaseUri = null,
    int MaxRetries = 3,
    TimeSpan? RequestTimeout = null)
{
    public Uri EffectiveBaseUri => BaseUri ?? new Uri("https://api.typesafe.ai/");
    public TimeSpan EffectiveRequestTimeout => RequestTimeout ?? TimeSpan.FromSeconds(5);

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(ApiKey))
        {
            throw new ArgumentException("TypeSafe API key is required.", nameof(ApiKey));
        }
        if (string.IsNullOrWhiteSpace(Model))
        {
            throw new ArgumentException("TypeSafe model is required.", nameof(Model));
        }
        if (MaxRetries is < 0 or > 10)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxRetries), "Max retries must be between 0 and 10.");
        }
        if (EffectiveRequestTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(RequestTimeout), "Request timeout must be positive.");
        }
    }
}
