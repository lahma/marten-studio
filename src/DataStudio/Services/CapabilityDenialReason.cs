namespace DataStudio.Services;

/// <summary>Why a capability was refused: the reason a module's capability guard names, in the shell because every module's guard has one.</summary>
internal enum CapabilityDenialReason
{
    /// <summary>The studio's master switch (<see cref="Settings.IShellSettings.ReadOnly" />) is on, so every capability is off.</summary>
    ReadOnly,

    /// <summary>The individual capability is off.</summary>
    Disabled
}
