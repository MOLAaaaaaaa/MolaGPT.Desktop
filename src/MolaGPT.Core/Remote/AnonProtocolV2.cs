using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace MolaGPT.Core.Remote;

/// <summary>
/// The pairing transcript of the anonymous protocol (§4.2 of the design):
/// commit, offer (signed by the device key), reveal, then the shared master
/// key, the six-digit SAS both screens show, and the AEAD-sealed finish
/// message. Pure functions — the host drives the state machine.
/// </summary>
public static class AnonPairingV2
{
    public static byte[] Commit(string deviceId, byte[] roomId, string room, byte[] handshakeId,
        byte[] grantPub, string phoneName, byte[] ephPubP, byte[] nP) =>
        AnonWire.Sha256("molagpt-anon-commit-v2", deviceId, roomId, room, handshakeId, grantPub, phoneName, ephPubP, nP);

    public static byte[] OfferSignatureInput(string deviceId, byte[] roomId, string room, byte[] handshakeId,
        byte[] commit, byte[] grantPub, string phoneName, byte[] ephPubD, byte[] nD, byte[] devicePub) =>
        AnonWire.E("molagpt-anon-offer-v2", deviceId, roomId, room, handshakeId, commit, grantPub,
            phoneName, ephPubD, nD, devicePub);

    public static byte[] Transcript(string deviceId, byte[] roomId, string room, byte[] handshakeId,
        byte[] commit, byte[] grantPub, string phoneName, byte[] ephPubP, byte[] nP, byte[] ephPubD, byte[] nD,
        byte[] devicePub) =>
        AnonWire.Sha256("molagpt-anon-pair-v2", deviceId, roomId, room, handshakeId, commit, grantPub,
            phoneName, ephPubP, nP, ephPubD, nD, devicePub);

    public sealed record MasterKeys(byte[] Master, byte[] SessionAuth, byte[] Confirm, byte[] Finish);

    public static MasterKeys DeriveMaster(byte[] z, byte[] transcript)
    {
        var master = AnonCrypto.Hkdf(z, transcript, AnonWire.E("molagpt-anon-master-v2"));
        return new MasterKeys(
            master,
            AnonCrypto.Hkdf(master, transcript, AnonWire.E("molagpt-anon-sas-key-v2")),
            AnonCrypto.Hkdf(master, transcript, AnonWire.E("molagpt-anon-confirm-v2")),
            AnonCrypto.Hkdf(master, transcript, AnonWire.E("molagpt-anon-finish-v2")));
    }

    /// <summary>Six digits, zero padded. Both ends show this; a mismatch means
    /// someone is in the middle.</summary>
    public static string SasCode(byte[] sessionAuthKey, byte[] transcript)
    {
        var mac = AnonCrypto.Hmac(sessionAuthKey, AnonWire.E("sas", transcript));
        var value = BinaryPrimitives.ReadUInt32BigEndian(mac);
        return (value % 1_000_000).ToString("D6");
    }

    public static string ConfirmValue(byte[] confirmKey, byte[] transcript, byte[] pairId) =>
        AnonText.ToBase64Url(AnonCrypto.Hmac(confirmKey, AnonWire.E("confirmed", transcript, pairId)));

    /// <summary>The finish message: sealed once per handshake under a
    /// per-handshake key, so the all-zero nonce is used exactly once.</summary>
    public static (string Ct, string Tag) SealFinish(byte[] finishKey, byte[] transcript, JsonObject payload)
    {
        var (ciphertext, tag) = AnonCrypto.Seal(finishKey, new byte[12], AnonWire.E("pair-finish", transcript),
            Encoding.UTF8.GetBytes(payload.ToJsonString()));
        return (AnonText.ToBase64Url(ciphertext), AnonText.ToBase64Url(tag));
    }

    public static JsonObject OpenFinish(byte[] finishKey, byte[] transcript, string ct, string tag)
    {
        var plaintext = AnonCrypto.Open(finishKey, new byte[12], AnonWire.E("pair-finish", transcript),
            AnonText.FromBase64Url(ct), AnonText.FromBase64Url(tag));
        return JsonNode.Parse(plaintext) as JsonObject
               ?? throw new CryptographicException("finish payload is not an object");
    }
}

/// <summary>
/// Steady-state sessions (§5.2, §5.3): each connection proves knowledge of the
/// pairing master, derives per-direction frame keys from fresh randoms, and
/// exchanges AES-GCM frames with strictly increasing sequence numbers.
/// </summary>
public static class AnonSessionV2
{
    public static byte[] ConnectKey(byte[] master, byte[] pairId) =>
        AnonCrypto.Hkdf(master, AnonWire.Sha256("pair", pairId), AnonWire.E("molagpt-anon-connect-v2"));

    public static string ProofPhone(byte[] connectKey, string deviceId, byte[] pairId, byte[] nP) =>
        AnonText.ToBase64Url(AnonCrypto.Hmac(connectKey,
            AnonWire.E("hello-p", "2", deviceId, pairId, nP)));

    public static string ProofDesktop(byte[] connectKey, string deviceId, byte[] pairId, byte[] nP, byte[] nD) =>
        AnonText.ToBase64Url(AnonCrypto.Hmac(connectKey,
            AnonWire.E("hello-d", "2", deviceId, pairId, nP, nD)));

    public sealed record SessionKeys(string Sid, byte[] S, byte[] PhoneToDesktop, byte[] DesktopToPhone);

    public static SessionKeys DeriveSession(byte[] master, string deviceId, byte[] pairId, byte[] nP, byte[] nD)
    {
        var s = AnonWire.Sha256("molagpt-anon-session-v2", "2", deviceId, pairId, nP, nD);
        return new SessionKeys(AnonText.ToBase64Url(s), s,
            AnonCrypto.Hkdf(master, s, AnonWire.E("molagpt-anon-frame-v2", "p2d")),
            AnonCrypto.Hkdf(master, s, AnonWire.E("molagpt-anon-frame-v2", "d2p")));
    }

    /// <summary>One direction's framing state: its key and the next sequence
    /// number to send or expect. The nonce is 0x00000000 || uint64_be(seq).</summary>
    public sealed class Direction(byte[] key, string deviceId, byte[] pairId, byte[] sid, string name)
    {
        public long NextSequence { get; set; }
        private byte[] Aad(long sequence) =>
            AnonWire.E("molagpt-anon-frame-v2", "2", deviceId, pairId, sid, name, sequence.ToString());

        private static byte[] Nonce(long sequence)
        {
            var nonce = new byte[12];
            BinaryPrimitives.WriteUInt64BigEndian(nonce.AsSpan(4), (ulong)sequence);
            return nonce;
        }

        public JsonObject Seal(JsonObject plaintext)
        {
            var sequence = NextSequence++;
            var (ciphertext, tag) = AnonCrypto.Seal(key, Nonce(sequence), Aad(sequence),
                Encoding.UTF8.GetBytes(plaintext.ToJsonString()));
            return new JsonObject
            {
                ["v"] = 2, ["pair"] = AnonText.ToBase64Url(pairId), ["sid"] = AnonText.ToBase64Url(sid),
                ["seq"] = sequence.ToString(), ["ct"] = AnonText.ToBase64Url(ciphertext), ["tag"] = AnonText.ToBase64Url(tag),
            };
        }

        /// <exception cref="CryptographicException">wrong tag, duplicate or skipped sequence.</exception>
        public JsonObject Open(JsonObject frame)
        {
            if (frame["v"] is not JsonValue v || !v.TryGetValue<int>(out var version) || version != 2)
                throw new CryptographicException("frame is not v2");
            var sequence = frame["seq"] is JsonValue s && s.TryGetValue<string>(out var text) && long.TryParse(text, out var parsed)
                ? parsed : throw new CryptographicException("bad seq");
            if (sequence != NextSequence) throw new CryptographicException($"out-of-order frame {sequence}");
            var ct = Text(frame, "ct") ?? throw new CryptographicException("missing ct");
            var tag = Text(frame, "tag") ?? throw new CryptographicException("missing tag");
            var plaintext = AnonCrypto.Open(key, Nonce(sequence), Aad(sequence),
                AnonText.FromBase64Url(ct), AnonText.FromBase64Url(tag));
            NextSequence++;
            return JsonNode.Parse(plaintext) as JsonObject ?? throw new CryptographicException("bad payload");
        }

        private static string? Text(JsonObject frame, string name) =>
            frame[name] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
    }
}
