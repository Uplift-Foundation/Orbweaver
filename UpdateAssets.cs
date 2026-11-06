using System.Runtime.InteropServices;

namespace Orbweaver
{
    // Which file of a release is this machine's installer (CB-308). Names are the
    // ones release.yml publishes — Orbweaver-<v>-<rid>.dmg and
    // Orbweaver-<v>-win-x64-setup.exe, plus SHA256SUMS.txt — read off the real
    // v1.0.3 release (tests/UnitTests/Fixtures/update), not remembered.
    internal static class UpdateAssets
    {
        internal const string ChecksumsName = "SHA256SUMS.txt";

        // The same answer as NeuralSpeech.EngineRid, lifted into a pure function
        // so every arm is a test rather than a thing the CI runner happens to be.
        internal static string RidFor(bool isMac, Architecture processArchitecture) =>
            isMac ? (processArchitecture == Architecture.Arm64 ? "osx-arm64" : "osx-x64") : "win-x64";

        internal static string CurrentRid() =>
            RidFor(OperatingSystem.IsMacOS(), RuntimeInformation.ProcessArchitecture);

        internal static string ExpectedAssetName(string version, string rid) =>
            rid.StartsWith("osx-", StringComparison.Ordinal)
                ? $"{Brand.ShortName}-{version}-{rid}.dmg"
                : $"{Brand.ShortName}-{version}-{rid}-setup.exe";

        // Names compare ordinally: a release also holds OrbweaverSpeech-*.zip
        // assets, and a loose "contains" would one day pick one of those.
        internal static (UpdateAsset Installer, UpdateAsset Checksums) Select(UpdateRelease release, string rid)
        {
            var wanted = ExpectedAssetName(release.Version, rid);
            var installer = release.Assets.FirstOrDefault(a => string.Equals(a.Name, wanted, StringComparison.Ordinal));
            if (installer is null)
                throw new UpdateException(UpdateFailure.NoInstaller,
                    $"Release {release.Tag} has no installer for this computer ({wanted}).");

            var sums = release.Assets.FirstOrDefault(a => string.Equals(a.Name, ChecksumsName, StringComparison.Ordinal));
            if (sums is null)
                throw new UpdateException(UpdateFailure.NoChecksums,
                    $"Release {release.Tag} has no {ChecksumsName}, so the download could not be verified.");

            return (installer, sums);
        }
    }
}
