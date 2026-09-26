using System;
using System.IO;
using System.Net.Sockets;
using System.Text;

namespace Orbis
{
    internal sealed partial class PairServer
    {
        internal volatile GameHit[] LibraryGames = new GameHit[0];
        internal Func<GameHit[]> RefreshLibrary;
        DateTime _libraryReadAt;

        bool HandleLibrary(NetworkStream stream, string method, string path, string prefix, string contentType, byte[] body)
        {
            if (method == "GET" && path == prefix + "/library") {
                if (RefreshLibrary != null && DateTime.UtcNow - _libraryReadAt > TimeSpan.FromSeconds(10)) {
                    // The phone server thread reads metadata; navigation never waits for it.
                    LibraryGames = RefreshLibrary(); _libraryReadAt = DateTime.UtcNow;
                }
                var json = new StringBuilder("{\"games\":[");
                foreach (var game in LibraryGames) {
                    if (game == null || !CustomCovers.ValidId(game.TitleId)) continue;
                    if (json.Length > 10) json.Append(',');
                    json.Append("{\"id\":\"").Append(game.TitleId).Append("\",\"name\":\"")
                        .Append(JsonLite.Escape(game.Name ?? game.TitleId)).Append("\",\"version\":\"")
                        .Append(JsonLite.Escape(game.Version ?? "")).Append("\",\"custom\":")
                        .Append(CustomCovers.Resolve(game.TitleId, null) != null ? "true" : "false").Append('}');
                }
                json.Append("]}"); WriteResponse(stream, 200, "application/json", json.ToString()); return true;
            }
            if (!path.StartsWith(prefix + "/cover/", StringComparison.Ordinal)) return false;
            string id = path.Substring(prefix.Length + 7);
            bool restore = id.EndsWith("/restore", StringComparison.Ordinal);
            if (restore) id = id.Substring(0, id.Length - 8);
            GameHit selected = null;
            foreach (var game in LibraryGames)
                if (game != null && game.TitleId == id && CustomCovers.ValidId(id)) { selected = game; break; }
            if (selected == null) { WriteResponse(stream, 404, "text/plain", "Game not found. Refresh Library after the PS4 has scanned installed games."); return true; }
            if (method == "POST") {
                byte[] artwork = body, icon = null;
                if (!restore && body != null && body.Length == CustomCovers.UploadByteCount) {
                    artwork = new byte[CustomCovers.ByteCount]; icon = new byte[CustomCovers.IconByteCount];
                    Buffer.BlockCopy(body, 0, artwork, 0, artwork.Length);
                    Buffer.BlockCopy(body, artwork.Length, icon, 0, icon.Length);
                }
                string error = restore ? CustomCovers.Restore(id) : contentType == "application/octet-stream"
                    ? CustomCovers.Save(id, artwork) : "Use Library to upload a fitted cover.";
                if (error != null) { PublishPhoneNotice(error, true); WriteResponse(stream, 400, "text/plain", error); return true; }
                System.Threading.Interlocked.Increment(ref Revision);
                Ps4CoverResult result = Ps4HomeCovers.Change(selected, icon, restore);
                PublishPhoneNotice((restore ? "Cover restored. " : "Cover saved. ") + result.Message, result.Status == "failed" || result.Status == "unavailable" || result.Status == "partial");
                WriteResponse(stream, 200, "application/json", "{\"saved\":true,\"sspi\":\"" + (restore ? "restored" : "saved") +
                    "\",\"ps4\":\"" + result.Status + "\",\"ps4Changed\":" + (result.Changed ? "true" : "false") +
                    ",\"message\":\"" + JsonLite.Escape((restore ? "Original SSPI cover restored. " : "SSPI cover saved. ") + result.Message) + "\"}");
                return true;
            }
            if (method == "GET" && !restore) {
                try {
                    string file = CustomCovers.Resolve(id, selected.ImageUrl);
                    // Only the scanner's local image or an owned upload is served.
                    // No request can supply a filename or make the server fetch a URL.
                    if (string.IsNullOrEmpty(file) || file.Contains("://") || !Path.IsPathRooted(file) || !File.Exists(file)) throw new IOException();
                    long size = new FileInfo(file).Length;
                    if (size < 8 || size > CoverImageDecoder.MaximumEncodedBytes) throw new IOException();
                    byte[] bytes = File.ReadAllBytes(file);
                    string type = bytes[0] == 137 && bytes[1] == 80 && bytes[2] == 78 && bytes[3] == 71 ? "image/png" :
                        bytes[0] == 255 && bytes[1] == 216 ? "image/jpeg" : null;
                    if (type == null) throw new IOException();
                    WriteResponseBytes(stream, 200, type, bytes);
                } catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException) {
                    WriteResponse(stream, 404, "text/plain", "No local cover available.");
                }
                return true;
            }
            WriteResponse(stream, 400, "text/plain", "Unsupported cover request."); return true;
        }
    }
}
