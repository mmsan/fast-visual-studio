using Microsoft.Win32.SafeHandles;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Net.WebSockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace FastVisualStudioBridge
{
    /// <summary>Admits this devenv process through the local Host before releasing editor messages.</summary>
    internal sealed class LocalBridgeAuthentication : IDisposable
    {
        private const int MaxBootstrapBytes = 16 * 1024;
        private const int MaxPlaintextBytes = 2 * 1024 * 1024;
        private const long MaxSequence = 9007199254740991;
        private const string Client = "visualstudio";
        private static readonly UTF8Encoding Utf8 = new UTF8Encoding(false, true);
        private readonly CngAesGcm outgoing;
        private readonly CngAesGcm incoming;
        private long sent;
        private long received;

        /// <summary>Imports independent directional keys for this authenticated connection.</summary>
        private LocalBridgeAuthentication(byte[] sendKey, byte[] receiveKey)
        {
            try
            {
                outgoing = new CngAesGcm(sendKey);
                incoming = new CngAesGcm(receiveKey);
            }
            catch
            {
                outgoing?.Dispose();
                throw;
            }
            finally
            {
                Array.Clear(sendKey, 0, sendKey.Length);
                Array.Clear(receiveKey, 0, receiveKey.Length);
            }
        }

        /// <summary>Obtains one short-lived ticket from a verified local Host pipe.</summary>
        internal static async Task<Bootstrap> BootstrapAsync(CancellationToken token)
        {
            using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                deadline.CancelAfter(TimeSpan.FromSeconds(5));
                var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "FastGoTo", "BrowserBridge", "Native", "bridge-host-v2.json");
                var metadata = ReadDescriptor(path);
                Require(Integer(metadata, "version") == 2);
                var pipeName = String(metadata, "pipe");
                Require(pipeName.StartsWith("FastGoTo.Bridge.v2.", StringComparison.Ordinal) && pipeName.Length <= 256);
                foreach (var character in pipeName)
                    Require(character >= 'a' && character <= 'z' || character >= 'A' && character <= 'Z' ||
                        character >= '0' && character <= '9' || character == '.' || character == '-');
                var hostPid = Integer(metadata, "pid");
                Require(hostPid > 0 && hostPid <= int.MaxValue);
                var hostImage = CanonicalImage(String(metadata, "executable"));
                Require(string.Equals(Path.GetFileName(hostImage), "FastGoTo.exe", StringComparison.OrdinalIgnoreCase));
                var nonce = new byte[32];
                using (var random = RandomNumberGenerator.Create()) random.GetBytes(nonce);
                var clientNonce = Hex(nonce);
                var peerPid = Process.GetCurrentProcess().Id;
                using (var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous))
                using (deadline.Token.Register(() => pipe.Dispose()))
                {
                    await pipe.ConnectAsync(5000, deadline.Token).ConfigureAwait(false);
                    Require(GetNamedPipeServerProcessId(pipe.SafePipeHandle, out var actualPid) && actualPid == hostPid);
                    using (var process = OpenProcess(0x1000, false, actualPid))
                    {
                        Require(!process.IsInvalid);
                        var image = new StringBuilder(32768);
                        var length = image.Capacity;
                        Require(QueryFullProcessImageName(process, 0, image, ref length));
                        Require(string.Equals(CanonicalImage(image.ToString()), hostImage, StringComparison.OrdinalIgnoreCase));
                        var request = Utf8.GetBytes(JsonConvert.SerializeObject(new
                        {
                            version = 2, type = "connect", client = Client, clientNonce
                        }) + "\n");
                        await pipe.WriteAsync(request, 0, request.Length, deadline.Token).ConfigureAwait(false);
                        await pipe.FlushAsync(deadline.Token).ConfigureAwait(false);
                        var buffer = new byte[MaxBootstrapBytes];
                        var count = 0;
                        while (true)
                        {
                            Require(count < buffer.Length);
                            var read = await pipe.ReadAsync(buffer, count, buffer.Length - count, deadline.Token).ConfigureAwait(false);
                            Require(read > 0);
                            var end = Array.IndexOf(buffer, (byte)'\n', count, read);
                            count += read;
                            if (end < 0) continue;
                            Require(end == count - 1);
                            var reply = Parse(buffer, end);
                            Require(Integer(reply, "version") == 2 && reply["ok"]?.Type == JTokenType.Boolean && (bool)reply["ok"]);
                            Require(String(reply, "client") == Client && String(reply, "clientNonce") == clientNonce);
                            Require(Integer(reply, "peerPid") == peerPid);
                            var ticket = String(reply, "ticket");
                            ValidateHex(ticket);
                            var keyText = String(reply, "key");
                            ValidateHex(keyText);
                            var expires = Integer(reply, "expires");
                            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                            Require(expires > now && expires <= now + 30000);
                            var port = Integer(reply, "port");
                            Require(port > 0 && port <= 65535);
                            deadline.Token.ThrowIfCancellationRequested();
                            return new Bootstrap(peerPid, ticket, DecodeHex(keyText), expires, (int)port, clientNonce);
                        }
                    }
                }
            }
        }

        /// <summary>Verifies the server, proves this client, and consumes the first encrypted ready frame.</summary>
        internal static async Task<LocalBridgeAuthentication> AuthenticateAsync(ClientWebSocket socket, Bootstrap bootstrap, CancellationToken token)
        {
            using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                deadline.CancelAfter(TimeSpan.FromSeconds(5));
                using (deadline.Token.Register(() => socket.Abort()))
                {
                    Require(bootstrap.Expires > DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
                    await SendJsonAsync(socket, new
                    {
                        type = "bridgeHello", protocol = 2, client = Client, peerPid = bootstrap.PeerPid,
                        ticket = bootstrap.Ticket, expires = bootstrap.Expires, clientNonce = bootstrap.ClientNonce
                    }, deadline.Token).ConfigureAwait(false);
                    var challenge = await ReceiveJsonAsync(socket, MaxBootstrapBytes, deadline.Token).ConfigureAwait(false);
                    Require(String(challenge, "type") == "bridgeChallenge" && Integer(challenge, "protocol") == 2);
                    var serverNonce = String(challenge, "serverNonce");
                    ValidateHex(serverNonce);
                    var proof = String(challenge, "proof");
                    ValidateHex(proof);
                    Require(FixedEquals(Derive(bootstrap, serverNonce, "server"), DecodeHex(proof)));
                    await SendJsonAsync(socket, new
                    {
                        type = "bridgeProof", proof = Hex(Derive(bootstrap, serverNonce, "client"))
                    }, deadline.Token).ConfigureAwait(false);
                    var authentication = new LocalBridgeAuthentication(Derive(bootstrap, serverNonce, "clientToServer"),
                        Derive(bootstrap, serverNonce, "serverToClient"));
                    try
                    {
                        var ready = Parse(Utf8.GetBytes(authentication.Decrypt(await ReceiveJsonAsync(socket,
                            MaxBootstrapBytes, deadline.Token).ConfigureAwait(false))));
                        Require(String(ready, "type") == "bridgeReady" && ready.Count == 1);
                        deadline.Token.ThrowIfCancellationRequested();
                        return authentication;
                    }
                    catch
                    {
                        authentication.Dispose();
                        throw;
                    }
                }
            }
        }

        /// <summary>Encrypts one application message; the caller serializes sends to preserve sequence order.</summary>
        internal byte[] Encrypt(byte[] plaintext)
        {
            Require(plaintext.Length <= MaxPlaintextBytes && sent < MaxSequence);
            var sequence = ++sent;
            return Utf8.GetBytes(JsonConvert.SerializeObject(new
            {
                type = "bridgeFrame", sequence,
                ciphertext = Convert.ToBase64String(outgoing.Transform(plaintext, Nonce(sequence), Aad("clientToServer", sequence), false))
            }));
        }

        /// <summary>Authenticates an exactly sequenced incoming frame before returning any plaintext.</summary>
        internal string Decrypt(JObject frame)
        {
            Require(String(frame, "type") == "bridgeFrame" && received < MaxSequence);
            var sequence = Integer(frame, "sequence");
            Require(sequence == received + 1);
            var text = String(frame, "ciphertext");
            Require(text.Length <= ((MaxPlaintextBytes + 18) / 3) * 4);
            byte[] ciphertext;
            try { ciphertext = Convert.FromBase64String(text); }
            catch (FormatException) { throw InvalidMessage(); }
            Require(ciphertext.Length > 16 && ciphertext.Length <= MaxPlaintextBytes + 16 && Convert.ToBase64String(ciphertext) == text);
            var plaintext = incoming.Transform(ciphertext, Nonce(sequence), Aad("serverToClient", sequence), true);
            received = sequence;
            try { return Utf8.GetString(plaintext); }
            catch (DecoderFallbackException) { throw InvalidMessage(); }
            finally { Array.Clear(plaintext, 0, plaintext.Length); }
        }

        /// <summary>Receives one bounded text frame without partial UTF-8 decoding or an application queue.</summary>
        internal static async Task<JObject> ReceiveJsonAsync(ClientWebSocket socket, int limit, CancellationToken token)
        {
            using (var message = new MemoryStream())
            {
                var buffer = new byte[8192];
                WebSocketReceiveResult result;
                do
                {
                    result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), token).ConfigureAwait(false);
                    Require(result.MessageType == WebSocketMessageType.Text && message.Length + result.Count <= limit);
                    await message.WriteAsync(buffer, 0, result.Count, token).ConfigureAwait(false);
                } while (!result.EndOfMessage);
                return Parse(message.ToArray());
            }
        }

        /// <summary>Receives and authenticates the next application frame with the protocol's plaintext bound.</summary>
        internal async Task<string> ReceiveAsync(ClientWebSocket socket, CancellationToken token)
        {
            return Decrypt(await ReceiveJsonAsync(socket, ((MaxPlaintextBytes + 18) / 3) * 4 + 128, token).ConfigureAwait(false));
        }

        /// <summary>Writes a small, non-sensitive handshake object.</summary>
        private static Task SendJsonAsync(ClientWebSocket socket, object message, CancellationToken token)
        {
            return socket.SendAsync(new ArraySegment<byte>(Utf8.GetBytes(JsonConvert.SerializeObject(message))),
                WebSocketMessageType.Text, true, token);
        }

        /// <summary>Reads bounded discovery metadata from the installed private path, rejecting reparse points.</summary>
        private static JObject ReadDescriptor(string path)
        {
            for (var current = path; !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
                Require((File.GetAttributes(current) & FileAttributes.ReparsePoint) == 0);
            using (var handle = CreateFile(path, 0x80000000, 7, IntPtr.Zero, 3, 0x00200000, IntPtr.Zero))
            {
                Require(!handle.IsInvalid);
                Require(GetFileInformationByHandleEx(handle, 9, out var info, Marshal.SizeOf<FileAttributeTagInfo>()));
                Require((info.Attributes & (FileAttributes.ReparsePoint | FileAttributes.Directory)) == 0);
                Require(string.Equals(FinalPath(handle), Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase));
                using (var file = new FileStream(handle, FileAccess.Read))
                {
                    Require(file.Length > 0 && file.Length <= MaxBootstrapBytes);
                    var bytes = new byte[(int)file.Length];
                    var offset = 0;
                    while (offset < bytes.Length)
                    {
                        var count = file.Read(bytes, offset, bytes.Length - offset);
                        Require(count > 0);
                        offset += count;
                    }
                    Require(file.ReadByte() == -1);
                    return Parse(bytes);
                }
            }
        }

        /// <summary>Resolves a local executable image through its open file handle.</summary>
        private static string CanonicalImage(string path)
        {
            Require(!string.IsNullOrEmpty(path) && path.Length < 32768 && Path.IsPathRooted(path) && !path.StartsWith("\\\\", StringComparison.Ordinal));
            using (var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                return FinalPath(file.SafeFileHandle);
        }

        /// <summary>Returns the canonical DOS path belonging to the same open file.</summary>
        private static string FinalPath(SafeFileHandle handle)
        {
            var path = new StringBuilder(32768);
            var length = GetFinalPathNameByHandle(handle, path, path.Capacity, 0);
            Require(length > 0 && length < path.Capacity);
            var result = path.ToString();
            Require(result.StartsWith("\\\\?\\", StringComparison.Ordinal) && !result.StartsWith("\\\\?\\UNC\\", StringComparison.OrdinalIgnoreCase));
            return result.Substring(4);
        }

        /// <summary>Parses strict UTF-8 without duplicate properties, date coercion, or unbounded nesting.</summary>
        private static JObject Parse(byte[] bytes, int count = -1)
        {
            try
            {
                using (var text = new StringReader(Utf8.GetString(bytes, 0, count < 0 ? bytes.Length : count)))
                using (var reader = new JsonTextReader(text) { DateParseHandling = DateParseHandling.None, MaxDepth = 32 })
                {
                    var result = JObject.Load(reader, new JsonLoadSettings { DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error });
                    Require(!reader.Read());
                    return result;
                }
            }
            catch (JsonException) { throw InvalidMessage(); }
            catch (DecoderFallbackException) { throw InvalidMessage(); }
        }

        /// <summary>Reads a required string without coercing other JSON types.</summary>
        private static string String(JObject value, string name)
        {
            Require(value[name]?.Type == JTokenType.String);
            return (string)value[name];
        }

        /// <summary>Reads a bounded integer without accepting floats or strings.</summary>
        private static long Integer(JObject value, string name)
        {
            Require(value[name]?.Type == JTokenType.Integer);
            try { return (long)value[name]; }
            catch (Exception error) when (error is OverflowException || error is InvalidCastException) { throw InvalidMessage(); }
        }

        /// <summary>Derives the exact protocol 2 proof or directional key.</summary>
        private static byte[] Derive(Bootstrap bootstrap, string nonce, string purpose)
        {
            using (var hmac = new HMACSHA256(bootstrap.Key))
                return hmac.ComputeHash(Utf8.GetBytes(JsonConvert.SerializeObject(new object[]
                { purpose, Client, bootstrap.PeerPid, bootstrap.Ticket, bootstrap.Expires, bootstrap.ClientNonce, nonce })));
        }

        /// <summary>Creates the fixed protocol AAD for one direction and sequence.</summary>
        private static byte[] Aad(string direction, long sequence)
        {
            return Utf8.GetBytes(JsonConvert.SerializeObject(new object[] { "fast-local-bridge-v2", direction, sequence }));
        }

        /// <summary>Creates four zero bytes followed by the big-endian unsigned 64-bit sequence.</summary>
        private static byte[] Nonce(long sequence)
        {
            var nonce = new byte[12];
            for (var index = 11; index >= 4; index--)
            {
                nonce[index] = (byte)sequence;
                sequence >>= 8;
            }
            return nonce;
        }

        /// <summary>Compares equal-length proof bytes without an early mismatch return.</summary>
        private static bool FixedEquals(byte[] left, byte[] right)
        {
            var different = left.Length ^ right.Length;
            for (var index = 0; index < Math.Min(left.Length, right.Length); index++) different |= left[index] ^ right[index];
            return different == 0;
        }

        /// <summary>Accepts only canonical lowercase 32-byte hexadecimal values.</summary>
        private static void ValidateHex(string value)
        {
            Require(value.Length == 64);
            foreach (var character in value) Require(character >= '0' && character <= '9' || character >= 'a' && character <= 'f');
        }

        /// <summary>Decodes previously validated canonical hexadecimal.</summary>
        private static byte[] DecodeHex(string value)
        {
            var bytes = new byte[value.Length / 2];
            for (var index = 0; index < bytes.Length; index++) bytes[index] = Convert.ToByte(value.Substring(index * 2, 2), 16);
            return bytes;
        }

        /// <summary>Encodes protocol values without separators or uppercase characters.</summary>
        private static string Hex(byte[] bytes)
        {
            return BitConverter.ToString(bytes).Replace("-", "").ToLowerInvariant();
        }

        /// <summary>Fails closed without including transport data in errors or logs.</summary>
        private static void Require(bool condition)
        {
            if (!condition) throw InvalidMessage();
        }

        /// <summary>Creates a payload-free authentication failure.</summary>
        private static CryptographicException InvalidMessage()
        {
            return new CryptographicException("Local bridge authentication failed.");
        }

        /// <summary>Releases both connection-specific native keys.</summary>
        public void Dispose()
        {
            outgoing.Dispose();
            incoming.Dispose();
        }

        /// <summary>Holds a single bootstrap in memory only until the socket authentication finishes.</summary>
        internal sealed class Bootstrap : IDisposable
        {
            internal readonly int PeerPid;
            internal readonly string Ticket;
            internal readonly byte[] Key;
            internal readonly long Expires;
            internal readonly int Port;
            internal readonly string ClientNonce;

            /// <summary>Records the broker-verified bootstrap fields for this connection attempt.</summary>
            internal Bootstrap(int peerPid, string ticket, byte[] key, long expires, int port, string nonce)
            {
                PeerPid = peerPid;
                Ticket = ticket;
                Key = key;
                Expires = expires;
                Port = port;
                ClientNonce = nonce;
            }

            /// <summary>Clears the bootstrap key once it is no longer needed.</summary>
            public void Dispose()
            {
                Array.Clear(Key, 0, Key.Length);
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct FileAttributeTagInfo
        {
            internal FileAttributes Attributes;
            internal uint ReparseTag;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint pid);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern SafeFileHandle OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint pid);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool QueryFullProcessImageName(SafeFileHandle process, uint flags, StringBuilder name, ref int length);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetFileInformationByHandleEx(SafeFileHandle file, int infoClass, out FileAttributeTagInfo info, int size);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern uint GetFinalPathNameByHandle(SafeFileHandle file, StringBuilder path, int size, uint flags);
    }

    /// <summary>Uses Windows CNG AES-256-GCM because .NET Framework 4.7.2 has no managed AesGcm API.</summary>
    internal sealed class CngAesGcm : IDisposable
    {
        private readonly object gate = new object();
        private IntPtr algorithm;
        private IntPtr key;

        /// <summary>Imports one 256-bit directional key into the native GCM provider.</summary>
        internal CngAesGcm(byte[] secret)
        {
            try
            {
                Check(BCryptOpenAlgorithmProvider(out algorithm, "AES", null, 0));
                var mode = Encoding.Unicode.GetBytes("ChainingModeGCM\0");
                Check(BCryptSetProperty(algorithm, "ChainingMode", mode, mode.Length, 0));
                Check(BCryptGenerateSymmetricKey(algorithm, out key, IntPtr.Zero, 0, secret, secret.Length, 0));
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        /// <summary>Transforms one bounded frame and appends or verifies its 16-byte authentication tag.</summary>
        internal byte[] Transform(byte[] data, byte[] nonce, byte[] aad, bool decrypt)
        {
            lock (gate)
            {
                if (key == IntPtr.Zero) throw new ObjectDisposedException(nameof(CngAesGcm));
                var inputLength = decrypt ? data.Length - 16 : data.Length;
                var output = new byte[inputLength];
                var tag = new byte[16];
                if (decrypt) Buffer.BlockCopy(data, inputLength, tag, 0, tag.Length);
                var nonceHandle = GCHandle.Alloc(nonce, GCHandleType.Pinned);
                var aadHandle = GCHandle.Alloc(aad, GCHandleType.Pinned);
                var tagHandle = GCHandle.Alloc(tag, GCHandleType.Pinned);
                try
                {
                    var info = new AuthInfo
                    {
                        Size = Marshal.SizeOf<AuthInfo>(), Version = 1,
                        Nonce = nonceHandle.AddrOfPinnedObject(), NonceLength = nonce.Length,
                        AuthData = aadHandle.AddrOfPinnedObject(), AuthDataLength = aad.Length,
                        Tag = tagHandle.AddrOfPinnedObject(), TagLength = tag.Length
                    };
                    int written;
                    var status = decrypt
                        ? BCryptDecrypt(key, data, inputLength, ref info, IntPtr.Zero, 0, output, output.Length, out written, 0)
                        : BCryptEncrypt(key, data, inputLength, ref info, IntPtr.Zero, 0, output, output.Length, out written, 0);
                    Check(status);
                    if (written != output.Length) throw new CryptographicException("Local bridge encryption failed.");
                    if (decrypt) return output;
                    var ciphertext = new byte[output.Length + tag.Length];
                    Buffer.BlockCopy(output, 0, ciphertext, 0, output.Length);
                    Buffer.BlockCopy(tag, 0, ciphertext, output.Length, tag.Length);
                    return ciphertext;
                }
                catch
                {
                    Array.Clear(output, 0, output.Length);
                    throw;
                }
                finally
                {
                    tagHandle.Free();
                    aadHandle.Free();
                    nonceHandle.Free();
                }
            }
        }

        /// <summary>Rejects all native cryptographic failures without logging key or frame material.</summary>
        private static void Check(int status)
        {
            if (status != 0) throw new CryptographicException("Local bridge encryption failed.");
        }

        /// <summary>Serializes key destruction against any in-flight frame transform.</summary>
        public void Dispose()
        {
            lock (gate)
            {
                if (key != IntPtr.Zero) { BCryptDestroyKey(key); key = IntPtr.Zero; }
                if (algorithm != IntPtr.Zero) { BCryptCloseAlgorithmProvider(algorithm, 0); algorithm = IntPtr.Zero; }
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct AuthInfo
        {
            internal int Size;
            internal int Version;
            internal IntPtr Nonce;
            internal int NonceLength;
            internal IntPtr AuthData;
            internal int AuthDataLength;
            internal IntPtr Tag;
            internal int TagLength;
            internal IntPtr MacContext;
            internal int MacContextLength;
            internal int AadLength;
            internal ulong DataLength;
            internal int Flags;
        }

        [DllImport("bcrypt.dll", CharSet = CharSet.Unicode)]
        private static extern int BCryptOpenAlgorithmProvider(out IntPtr algorithm, string name, string implementation, uint flags);
        [DllImport("bcrypt.dll", CharSet = CharSet.Unicode)]
        private static extern int BCryptSetProperty(IntPtr handle, string property, byte[] value, int size, uint flags);
        [DllImport("bcrypt.dll")]
        private static extern int BCryptGenerateSymmetricKey(IntPtr algorithm, out IntPtr key, IntPtr keyObject, int objectSize, byte[] secret, int secretSize, uint flags);
        [DllImport("bcrypt.dll")]
        private static extern int BCryptEncrypt(IntPtr key, byte[] input, int inputSize, ref AuthInfo info, IntPtr iv, int ivSize, byte[] output, int outputSize, out int written, uint flags);
        [DllImport("bcrypt.dll")]
        private static extern int BCryptDecrypt(IntPtr key, byte[] input, int inputSize, ref AuthInfo info, IntPtr iv, int ivSize, byte[] output, int outputSize, out int written, uint flags);
        [DllImport("bcrypt.dll")]
        private static extern int BCryptDestroyKey(IntPtr key);
        [DllImport("bcrypt.dll")]
        private static extern int BCryptCloseAlgorithmProvider(IntPtr algorithm, uint flags);
    }
}
