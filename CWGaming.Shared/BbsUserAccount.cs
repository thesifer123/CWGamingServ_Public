using System;
using System.Collections.Generic;

namespace CWGaming.Shared;

/// <summary>
/// A BBS board account: login credentials, sysop/test flags, and last-seen IP. Owned by the BBS host and
/// deliberately door-agnostic — it does NOT know which character (if any) it plays. The account↔character
/// link lives on the door side (each door stores the owning BBS user id on its own player record), so the
/// BBS never stores a player name and adding a second door/realm needs no new fields here. Generic — no
/// game concepts.
/// </summary>
public sealed class BbsUserAccount
{
    // Board super-sysops: these login names are ALWAYS BBS sysops. Separate from any door's own
    // game-sysop list — this grants BOARD access only, never any game power. Because the check lives in
    // the IsSysop getter, access auto-restores on load even if the persisted bit is 0 and cannot be
    // revoked by ;access for a name in this set.
    //
    // Empty by default, and it must stay that way in a public build — any name listed here is a standing
    // back door for whoever registers it. Populate it only in your own deployment, with the operator
    // accounts you actually control (or grant sysop with ;access instead).
    public static readonly HashSet<string> SuperSysopNames = new(StringComparer.OrdinalIgnoreCase)
    {
    };

    public string UserName { get; set; } = "";

    /// <summary>
    /// The name shown to other users while online (WHO list, greetings). Distinct from the login
    /// <see cref="UserName"/> so a member can present a handle without exposing their sign-in name.
    /// Defaults to the login name on account creation until the member changes it; never blank.
    /// </summary>
    public string DisplayName { get; set; } = "";

    public string PasswordHash { get; set; } = "";

    private bool _isSysop;

    /// <summary>
    /// Whether this account has BBS sysop access. Returns true for any <see cref="SuperSysopNames"/> login
    /// regardless of the persisted bit, so a super-sysop's access can never be lost or revoked.
    /// </summary>
    public bool IsSysop
    {
        get => _isSysop || SuperSysopNames.Contains(UserName);
        set => _isSysop = value;
    }

    /// <summary>True when this login is a hardcoded board super-sysop (see <see cref="SuperSysopNames"/>).</summary>
    public bool IsSuperSysop => SuperSysopNames.Contains(UserName);

    public bool IsTestAccount { get; set; }
    public string LastIpAddress { get; set; } = "";
}
