namespace ScreenTail.Api.Publish;

/// <summary>How long one publish may take, on the deployment's side.</summary>
public sealed class PublishOptions
{
    public TimeSpan Deadline { get; set; } = TimeSpan.FromSeconds(75);
}
