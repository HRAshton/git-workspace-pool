using System.Text.Json;
using System.Text.Json.Serialization;
using GitWorkspacePool.Core.Domain;

namespace GitWorkspacePool.Tray.Infrastructure;

internal sealed class AppSettings
{
    public List<GitRepositoryRegistration> GitRepositories { get; set; } = [];
    public string? EditorExecutable { get; set; } = "code.exe";
    public int OperationTimeoutMinutes { get; set; } = 15;
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? OtherSettings { get; set; }
}
