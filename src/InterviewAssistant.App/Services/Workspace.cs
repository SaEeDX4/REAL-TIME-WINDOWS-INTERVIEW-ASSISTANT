using System.Security.Cryptography;
using InterviewAssistant.Core.Domain;
using InterviewAssistant.Core.Knowledge;
using InterviewAssistant.Core.Persistence;
using InterviewAssistant.Core.Preparation;

namespace InterviewAssistant.App.Services;

/// <summary>DPAPI (CurrentUser) protection for workspace files: profiles, résumés, packs and sessions are encrypted at rest.</summary>
public sealed class DpapiProtector : IDataProtector
{
    private static readonly byte[] Entropy = "InterviewAssistant.workspace.v1"u8.ToArray();
    public byte[] Protect(byte[] plain) => ProtectedData.Protect(plain, Entropy, DataProtectionScope.CurrentUser);
    public byte[] Unprotect(byte[] cipher) => ProtectedData.Unprotect(cipher, Entropy, DataProtectionScope.CurrentUser);
}

/// <summary>Local multi-profile workspace + the shared preparation assets shipped with the app.</summary>
public sealed class Workspace
{
    public WorkspaceStore Store { get; }
    public PreparationAssets Assets { get; }
    public string? KnowledgeDir { get; }
    public string? SamplesDir { get; }

    public Workspace(string root, IDataProtector protector)
    {
        Store = new WorkspaceStore(root, protector);
        KnowledgeDir = KnowledgeBase.FindDirectory(AppContext.BaseDirectory, "knowledge");
        SamplesDir = KnowledgeBase.FindDirectory(AppContext.BaseDirectory, "samples");
        Assets = KnowledgeDir != null && File.Exists(Path.Combine(KnowledgeDir, "generic_question_library.json")) ? PreparationAssets.Load(KnowledgeDir) : new PreparationAssets();
    }

    /// <summary>Knowledge for the selected profile/target. Falls back to generic (no candidate claims) when nothing is prepared.</summary>
    public (KnowledgeBase Kb, string Label) LoadActive(string? profileId, string? targetId)
    {
        if (profileId != null && targetId != null)
        {
            var profile = Store.GetProfile(profileId);
            var target = Store.GetTarget(targetId);
            var pack = target != null && target.ProfileId == profileId ? Store.GetPack(targetId) : null;
            if (profile != null && target != null && pack != null)
                return (pack.ToKnowledgeBase(), $"{(profile.PreferredName.Length > 0 ? profile.PreferredName : profile.Name)} · {target.DisplayName}");
        }
        return (KnowledgeBase.Empty(Assets.Playbooks), "No interview prepared");
    }

    /// <summary>The built-in sample (test fixture) — loaded only on explicit request or for self-tests.</summary>
    public (KnowledgeBase Kb, string Label)? LoadSample()
    {
        var dir = SamplesDir == null ? null : Path.Combine(SamplesDir, "shervin-teroxx");
        if (dir == null || !File.Exists(Path.Combine(dir, "question_bank.json"))) return null;
        var kb = KnowledgeBase.Load(dir);
        return (kb, $"Sample · {kb.Context.CandidateName} · {kb.Context.CompanyName}");
    }
}
