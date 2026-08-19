using UsageTray.Core;
using UsageTray.Data;

namespace UsageTray.Services;

public sealed class ProjectService
{
    private readonly UsageRepository _repository;

    public ProjectService(UsageRepository repository)
    {
        _repository = repository;
    }

    public void SaveAlias(ProviderKind provider, string projectKey, string displayName)
    {
        if (string.IsNullOrWhiteSpace(projectKey) || string.IsNullOrWhiteSpace(displayName)) return;
        _repository.SetAlias(provider, projectKey, displayName.Trim());
    }
}
