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

    public Workspace(string root, IDataProtector protector)
    {
        Store = new WorkspaceStore(root, protector);
        // Shipped generic assets: knowledge\generic_question_library.json + playbooks (no candidate data).
        KnowledgeDir = KnowledgeBase.FindDirectory(AppContext.BaseDirectory, "knowledge", PreparationAssets.LibraryFileName);
        Assets = KnowledgeDir != null ? PreparationAssets.Load(KnowledgeDir) : new PreparationAssets();
        if (Assets.Library.Count == 0) AppLog.Warn("Generic preparation library not found next to the app — preparation will produce fewer questions.");
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

    /// <summary>Loads a fixture pack directory (test/sample data is NOT shipped in the product; self-tests pass the path).</summary>
    public (KnowledgeBase Kb, string Label)? LoadSample(string? dir)
    {
        if (dir == null || !File.Exists(Path.Combine(dir, "question_bank.json"))) return null;
        var kb = KnowledgeBase.Load(dir);
        return (kb, $"Sample · {kb.Context.CandidateName} · {kb.Context.CompanyName}");
    }
}
