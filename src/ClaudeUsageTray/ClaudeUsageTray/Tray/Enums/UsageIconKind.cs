namespace ClaudeUsageTray.Tray.Enums;

/// <summary>The two usage icons. Not the <c>NOTIFYICONDATA.uID</c>: that is the position slot (see UsageTrayController).</summary>
public enum UsageIconKind : uint
{
    Session = 1,
    Weekly = 2,
}
