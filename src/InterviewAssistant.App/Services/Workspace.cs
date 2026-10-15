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

/// <summary>
/// The signed-in account's local workspace (profiles, interviews, prepared answers, reports — DPAPI-encrypted) plus the
/// shared preparation assets shipped with the app. One workspace per account (<see cref="WorkspaceLocator"/>).
/// </summary>
public sealed class Workspace
{
    public WorkspaceStore Store { get; }
    public PreparationAssets Assets { get; }
    public string? KnowledgeDir { get; }
    /// <summary>Account id hash (or "local"/"selftest") this workspace belongs to.</summary>
    public string Owner { get; init; } = "local";

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
            {
                var kb = pack.ToKnowledgeBase();
                // Personal answers and boundaries are always the CURRENT confirmed ones (editing them never needs re-preparation).
                kb.Profile.Personal.Clear(); kb.Profile.Personal.AddRange(ProfileService.PersonalEntries(profile));
                kb.Profile.Boundaries.Clear(); kb.Profile.Boundaries.AddRange(ProfileService.BoundaryEntries(profile));
                return (kb, $"{(profile.PreferredName.Length > 0 ? profile.PreferredName : profile.Name)} · {target.DisplayName}");
            }
        }
        return (KnowledgeBase.Empty(Assets.Playbooks), Core.Languages.Loc.T("no_interview_prepared"));
    }

    /// <summary>The interview selected for the Live Assistant in this workspace.</summary>
    public (KnowledgeBase Kb, string Label) LoadActive()
    {
        var (p, t) = Store.GetActive();
        return LoadActive(p, t);
    }

    /// <summary>Loads a fixture pack directory (test/sample data is NOT shipped in the product; self-tests pass the path).</summary>
    public static (KnowledgeBase Kb, string Label)? LoadSample(string? dir)
    {
        if (dir == null || !File.Exists(Path.Combine(dir, "question_bank.json"))) return null;
        var kb = KnowledgeBase.Load(dir);
        return (kb, $"Sample · {kb.Context.CandidateName} · {kb.Context.CompanyName}");
    }
}
