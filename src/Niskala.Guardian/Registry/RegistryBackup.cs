namespace Niskala.Guardian.Registry;

public sealed record RegistryValueBackup(string Name, int Kind, byte[] Data);

public sealed record RegistryKeyBackup(string Name, IReadOnlyList<RegistryValueBackup> Values, IReadOnlyList<RegistryKeyBackup> SubKeys);

public sealed record RegistryBackupEntry(string Path, RegistryKeyBackup? Key, RegistryValueBackup? Value);

public sealed record RegistryBackup(IReadOnlyList<RegistryBackupEntry> Entries);
