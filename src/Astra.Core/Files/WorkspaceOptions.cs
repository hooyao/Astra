namespace Astra.Core.Files;

public sealed class WorkspaceOptions
{
    public const string SectionName = "Tools";

    public string WorkingDirectory { get; set; } = ".";
    public List<string> AllowedRoots { get; set; } = [];
}
