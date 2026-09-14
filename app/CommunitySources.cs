using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace Orbis
{
    internal sealed class CommunitySourceEntry
    {
        internal string Id, Name, Message, Tags, Date;
        internal long Size;
    }
    internal static class CommunitySources
    {
        internal static string Endpoint { get { return DistributionSettings.DirectoryEndpoint; } }
        const string Client = "SSPI-community-v1";
        // Include the authenticated envelope, base64 expansion and {"blob":""} wrapper.
        internal const int MaximumDownloadResponseBytes =
            ((PackageSourcePackage.MaximumCompressedBytes + 80 + 2) / 3) * 4 + 11;
        // Shared by public clients: hides raw storage, not a confidentiality boundary.
        const string Seed = "SSPI community source envelope v1 · shared directory";
        internal static bool ValidId(string id)
        {
            if (id == null || id.Length != 64) return false;
            foreach (char c in id) if (!(c >= 'a' && c <= 'f') && !(c >= '0' && c <= '9')) return false;
            return true;
        }
        internal static string Text(Dictionary<string, object> d, string key, int max)
        {
            object o; string s = d.TryGetValue(key, out o) ? o as string : null;
            var b = new StringBuilder(); foreach (char c in s ?? "") { if (!char.IsControl(c)) b.Append(c); if (b.Length >= max) break; }
            return b.ToString();
        }
        internal static byte[] OpenEnvelope(string encoded, int maximum)
        {
            if (encoded == null || encoded.Length > (maximum + 80L) * 4 / 3 + 4) throw new InvalidDataException("Source envelope exceeds limit");
            byte[] bytes = Convert.FromBase64String(encoded);
            if (bytes.Length < 65 || bytes.Length > maximum + 80 || bytes[0] != 1 || (bytes.Length - 49) % 16 != 0)
                throw new InvalidDataException("Invalid source envelope");
            byte[] keys; using (var sha = SHA512.Create()) keys = sha.ComputeHash(Encoding.UTF8.GetBytes(Seed));
            var encryption = new byte[32]; var authentication = new byte[32]; var iv = new byte[16];
            Buffer.BlockCopy(keys, 0, encryption, 0, 32); Buffer.BlockCopy(keys, 32, authentication, 0, 32); Buffer.BlockCopy(bytes, 1, iv, 0, 16);
            byte[] tag; using (var mac = new HMACSHA256(authentication)) tag = mac.ComputeHash(bytes, 0, bytes.Length - 32);
            int difference = 0; for (int i = 0; i < 32; i++) difference |= tag[i] ^ bytes[bytes.Length - 32 + i];
            if (difference != 0) throw new InvalidDataException("Source authentication failed");
            using (var aes = new AesManaged { Key = encryption, IV = iv, Mode = CipherMode.CBC, Padding = PaddingMode.PKCS7 })
            using (var decrypt = aes.CreateDecryptor()) {
                byte[] plain = decrypt.TransformFinalBlock(bytes, 17, bytes.Length - 49);
                if (plain.Length > maximum) throw new InvalidDataException("Source exceeds limit");
                return plain;
            }
        }
        internal static List<CommunitySourceEntry> List(string after, out string next)
        {
            if (!string.IsNullOrEmpty(after) && !ValidId(after)) throw new InvalidDataException("Invalid directory page");
            string response = NetHttp.GetStringDirect(Endpoint + "list?after=" + (after ?? ""), 30000, null, Client);
            if (response.Length > 400000) throw new InvalidDataException("Directory response too large");
            var root = PackageSourceJson.Parse(response) as Dictionary<string, object>;
            if (root == null) throw new InvalidDataException("Invalid directory response");
            next = Text(root, "next", 64); if (next.Length > 0 && !ValidId(next)) throw new InvalidDataException("Invalid next page");
            object value; var rows = root.TryGetValue("items", out value) ? value as IList : null;
            if (rows == null || rows.Count > 30) throw new InvalidDataException("Invalid directory entries");
            var result = new List<CommunitySourceEntry>();
            foreach (object row in rows) {
                try {
                    var d = row as Dictionary<string, object>; if (d == null) continue;
                    string id = Text(d, "id", 64); if (!ValidId(id)) continue;
                    string encoded = Text(d, "meta", 12000);
                    var metadata = PackageSourceJson.Parse(Encoding.UTF8.GetString(OpenEnvelope(encoded, 8192))) as Dictionary<string, object>;
                    if (metadata == null || Text(metadata,"id",64) != id) continue;
                    long size = Convert.ToInt64(d["size"]); if (size < 1 || size > PackageSourcePackage.MaximumCompressedBytes) continue;
                    var tags = new List<string>(); object raw; var list = metadata.TryGetValue("tags", out raw) ? raw as IList : null;
                    if (list != null) foreach (var tag in list) { if (tag is string && tags.Count < 5) tags.Add(Text(new Dictionary<string, object>{{"t",tag}},"t",24)); }
                    result.Add(new CommunitySourceEntry { Id=id, Name=Text(metadata,"name",80), Message=Text(metadata,"message",500), Tags=string.Join(" · ",tags.ToArray()), Date=Text(d,"date",10),Size=size });
                } catch { /* An invalid submission must not break the whole page. */ }
            }
            return result;
        }
        internal static byte[] Download(CommunitySourceEntry entry)
        {
            if (entry == null || !ValidId(entry.Id)) throw new InvalidDataException("Invalid source ID");
            string response = NetHttp.GetStringDirect(Endpoint + "file/" + entry.Id, 45000,
                null, Client, null, MaximumDownloadResponseBytes);
            if (response.Length > MaximumDownloadResponseBytes) throw new InvalidDataException("Source response too large");
            // This endpoint has exactly one base64 field. Keep large file data out
            // of the manifest parser, whose deliberate limit is only 512 KiB.
            if (!response.StartsWith("{\"blob\":\"", StringComparison.Ordinal) || !response.EndsWith("\"}", StringComparison.Ordinal))
                throw new InvalidDataException("Source was removed or unavailable");
            byte[] bytes = OpenEnvelope(response.Substring(9, response.Length - 11), PackageSourcePackage.MaximumCompressedBytes);
            string hash; using (var sha = SHA256.Create()) hash = BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant();
            if (bytes.Length != entry.Size || hash != entry.Id) throw new InvalidDataException("Source identity mismatch");
            PackageSourcePackage.Open(bytes); // existing declarative schema/archive guards
            return bytes;
        }
    }
}
