// Copyright (C) 2026 Touché PX5 contributors
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;

namespace Touche.Firmware;

public interface IFirmwareVirtualFileSystem
{
    bool Exists(string virtualPath);

    FirmwareArtifact? GetArtifact(string virtualPath);

    ValueTask<FirmwareFileHandle?> OpenReadAsync(
        string virtualPath,
        CancellationToken cancellationToken = default);

    ValueTask<bool> VerifyAsync(
        string virtualPath,
        CancellationToken cancellationToken = default);
}

public sealed class FirmwareFileHandle : IAsyncDisposable, IDisposable
{
    private readonly Stream _stream;

    internal FirmwareFileHandle(FirmwareArtifact artifact, Stream stream)
    {
        Artifact = artifact;
        _stream = new NonDisclosingReadStream(stream);
    }

    public FirmwareArtifact Artifact { get; }

    public Stream Content => _stream;

    public void Dispose() => _stream.Dispose();

    public ValueTask DisposeAsync() => _stream.DisposeAsync();

    private sealed class NonDisclosingReadStream(Stream inner) : Stream
    {
        public override bool CanRead => inner.CanRead;

        public override bool CanSeek => inner.CanSeek;

        public override bool CanWrite => false;

        public override long Length => inner.Length;

        public override long Position
        {
            get => inner.Position;
            set => inner.Position = value;
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);

        public override int Read(Span<byte> buffer) => inner.Read(buffer);

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default) => inner.ReadAsync(buffer, cancellationToken);

        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);

        public override void SetLength(long value) => throw new NotSupportedException("Firmware handles are read-only.");

        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException("Firmware handles are read-only.");

        public override void Write(ReadOnlySpan<byte> buffer) =>
            throw new NotSupportedException("Firmware handles are read-only.");

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                inner.Dispose();
            }
            base.Dispose(disposing);
        }

        public override async ValueTask DisposeAsync()
        {
            await inner.DisposeAsync().ConfigureAwait(false);
            GC.SuppressFinalize(this);
        }
    }
}

/// <summary>
/// Read-only guest-path view over one imported firmware profile. Physical CAS
/// paths remain private to this implementation.
/// </summary>
public sealed class FirmwareVirtualFileSystem : IFirmwareVirtualFileSystem
{
    private readonly string _objectsRoot;
    private readonly IReadOnlyDictionary<string, FirmwareArtifact> _artifacts;
    private readonly ConcurrentDictionary<string, ObjectFingerprint> _verifiedObjects = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _verificationGates = new(StringComparer.Ordinal);

    private FirmwareVirtualFileSystem(
        string objectsRoot,
        IReadOnlyDictionary<string, FirmwareArtifact> artifacts)
    {
        _objectsRoot = objectsRoot;
        _artifacts = artifacts;
    }

    public static FirmwareVirtualFileSystem Mount(string storeRoot, string profileId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(storeRoot);
        ValidateProfileId(profileId);
        var repository = new FirmwareProfileRepository(storeRoot);
        var profile = repository.GetImportedProfiles().FirstOrDefault(candidate =>
            string.Equals(candidate.ProfileId, profileId, StringComparison.Ordinal));
        if (profile is null)
        {
            throw new DirectoryNotFoundException($"Firmware profile is not installed: {profileId}");
        }

        var manifestPath = Path.Combine(profile.ProfileDirectory, "manifest.json");
        var manifest = JsonSerializer.Deserialize<FirmwareProfileManifest>(File.ReadAllText(manifestPath))
            ?? throw new InvalidDataException($"Firmware profile manifest is invalid: {profileId}");
        if (!string.Equals(manifest.ProfileId, profileId, StringComparison.Ordinal) || manifest.Artifacts is null)
        {
            throw new InvalidDataException($"Firmware profile manifest does not match its installation: {profileId}");
        }

        var artifacts = new Dictionary<string, FirmwareArtifact>(StringComparer.Ordinal);
        foreach (var artifact in manifest.Artifacts)
        {
            var normalized = NormalizeVirtualPath(artifact.VirtualPath);
            if (!string.Equals(normalized, artifact.VirtualPath, StringComparison.Ordinal) ||
                !artifacts.TryAdd(normalized, artifact))
            {
                throw new InvalidDataException($"Firmware profile contains a duplicate or non-canonical path: {artifact.VirtualPath}");
            }
        }

        return new FirmwareVirtualFileSystem(
            Path.Combine(Path.GetFullPath(storeRoot), "objects"),
            artifacts);
    }

    public bool Exists(string virtualPath)
    {
        var normalized = NormalizeVirtualPath(virtualPath);
        return _artifacts.TryGetValue(normalized, out var artifact) && File.Exists(GetObjectPath(artifact));
    }

    public FirmwareArtifact? GetArtifact(string virtualPath)
    {
        var normalized = NormalizeVirtualPath(virtualPath);
        return _artifacts.GetValueOrDefault(normalized);
    }

    public ValueTask<FirmwareFileHandle?> OpenReadAsync(
        string virtualPath,
        CancellationToken cancellationToken = default) =>
        OpenReadCoreAsync(virtualPath, forceVerification: false, cancellationToken);

    public async ValueTask<bool> VerifyAsync(
        string virtualPath,
        CancellationToken cancellationToken = default)
    {
        await using var handle = await OpenReadCoreAsync(
            virtualPath,
            forceVerification: true,
            cancellationToken).ConfigureAwait(false);
        return handle is not null;
    }

    internal static string NormalizeVirtualPath(string virtualPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(virtualPath);
        if (virtualPath[0] != '/' ||
            virtualPath.Length == 1 ||
            virtualPath.Contains('\\') ||
            virtualPath.Contains('\0') ||
            virtualPath.Contains("//", StringComparison.Ordinal))
        {
            throw new InvalidDataException($"Invalid firmware virtual path: {virtualPath}");
        }

        var components = virtualPath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (components.Any(component => component is "." or ".." || component.Length == 0))
        {
            throw new InvalidDataException($"Invalid firmware virtual path: {virtualPath}");
        }

        return "/" + string.Join('/', components);
    }

    private async ValueTask<FirmwareFileHandle?> OpenReadCoreAsync(
        string virtualPath,
        bool forceVerification,
        CancellationToken cancellationToken)
    {
        var normalized = NormalizeVirtualPath(virtualPath);
        if (!_artifacts.TryGetValue(normalized, out var artifact))
        {
            return null;
        }

        var objectPath = GetObjectPath(artifact);
        if (!File.Exists(objectPath))
        {
            throw new FileNotFoundException("Firmware CAS object is missing.");
        }
        if ((File.GetAttributes(objectPath) & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException("Firmware CAS objects cannot be links or reparse points.");
        }

        var gate = _verificationGates.GetOrAdd(artifact.Sha256, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        FileStream? stream = null;
        try
        {
            stream = new FileStream(
                objectPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                1024 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            if (stream.Length != artifact.Size)
            {
                throw new InvalidDataException("Firmware CAS object size does not match its manifest.");
            }

            var fingerprint = new ObjectFingerprint(
                stream.Length,
                File.GetLastWriteTimeUtc(objectPath).Ticks);
            if (forceVerification ||
                !_verifiedObjects.TryGetValue(artifact.Sha256, out var cached) ||
                cached != fingerprint)
            {
                var actualHash = await ComputeHashAsync(stream, cancellationToken).ConfigureAwait(false);
                if (!string.Equals(actualHash, artifact.Sha256, StringComparison.Ordinal))
                {
                    _verifiedObjects.TryRemove(artifact.Sha256, out _);
                    throw new InvalidDataException("Firmware CAS object failed SHA-256 verification.");
                }

                _verifiedObjects[artifact.Sha256] = fingerprint;
            }

            stream.Position = 0;
            var handle = new FirmwareFileHandle(artifact, stream);
            stream = null;
            return handle;
        }
        finally
        {
            if (stream is not null)
            {
                await stream.DisposeAsync().ConfigureAwait(false);
            }
            gate.Release();
        }
    }

    private string GetObjectPath(FirmwareArtifact artifact)
    {
        if (artifact.Sha256.Length != 64 || !artifact.Sha256.All(char.IsAsciiHexDigit))
        {
            throw new InvalidDataException("Firmware artifact contains an invalid object hash.");
        }

        return Path.Combine(_objectsRoot, artifact.Sha256[..2], artifact.Sha256);
    }

    private static async Task<string> ComputeHashAsync(Stream stream, CancellationToken cancellationToken)
    {
        stream.Position = 0;
        using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[1024 * 1024];
        while (true)
        {
            var read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            hasher.AppendData(buffer, 0, read);
        }

        return Convert.ToHexString(hasher.GetHashAndReset()).ToLowerInvariant();
    }

    private static void ValidateProfileId(string profileId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profileId);
        const string prefix = "ps5-extracted-";
        if (!profileId.StartsWith(prefix, StringComparison.Ordinal) ||
            profileId.Length != prefix.Length + 64 ||
            !profileId[prefix.Length..].All(char.IsAsciiHexDigit))
        {
            throw new InvalidDataException($"Invalid firmware profile ID: {profileId}");
        }
    }

    private readonly record struct ObjectFingerprint(long Size, long LastWriteTicks);
}
