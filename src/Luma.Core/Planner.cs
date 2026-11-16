namespace Luma.Core;

public static class Planner
{
    static readonly Dictionary<string, string[]> VideoContainers = new()
    {
        ["mp4"] = ["h264", "hevc", "mpeg4", "av1", "vp9", "mjpeg"],
        ["mov"] = ["h264", "hevc", "mpeg4", "prores", "mjpeg", "av1"],
        ["avi"] = ["mpeg4", "h264", "mjpeg"], ["webm"] = ["vp8", "vp9", "av1"],
        ["flv"] = ["h264", "flv1"], ["ts"] = ["h264", "hevc", "mpeg2video"]
    };
    static readonly Dictionary<string, string[]> AudioContainers = new()
    {
        ["mp4"] = ["aac", "mp3", "ac3", "eac3", "alac"], ["mov"] = ["aac", "mp3", "ac3", "alac", "pcm_s16le", "pcm_s24le"],
        ["avi"] = ["mp3", "ac3", "pcm_s16le"], ["webm"] = ["opus", "vorbis"], ["flv"] = ["aac", "mp3"],
        ["ts"] = ["aac", "mp2", "mp3", "ac3", "eac3"]
    };
    public static bool Compatible(string container, MediaStream stream)
    {
        if (container == "mkv") return stream.Type switch
        { "video" => stream.Codec is not ("unknown" or ""), "audio" => stream.Codec is not ("unknown" or ""),
          "subtitle" => stream.Codec is "subrip" or "ass" or "ssa" or "webvtt" or "hdmv_pgs_subtitle" or "dvd_subtitle" or "dvb_subtitle", _ => false };
        if (stream.Type == "video") return VideoContainers.TryGetValue(container, out var v) && v.Contains(stream.Codec);
        if (stream.Type == "audio") return AudioContainers.TryGetValue(container, out var a) && a.Contains(stream.Codec);
        return stream.Type == "subtitle" && container switch { "mp4" or "mov" => stream.Codec == "mov_text", "webm" => stream.Codec == "webvtt", "ts" => stream.Codec == "dvb_subtitle", _ => false };
    }
    public static long? Target(MediaInfo m, JobOptions o) => o.Operation == Operation.Compress && o.Compression != CompressionMode.Quality
        ? o.Compression == CompressionMode.Reduction ? (long)(m.Size * (1 - o.Reduction / 100)) : o.TargetBytes : null;
    public static int TargetVideoKbps(long target, double duration, int audioKbps) => duration > 0
        ? (int)Math.Clamp(Math.Floor(target * .97 * 8 / duration / 1000 - audioKbps), int.MinValue, int.MaxValue) : 0;
    public static EncodePlan Create(MediaInfo m, JobOptions o, bool qsvAvailable = false, bool forceCpu = false)
    {
        if (m.Video == null && o.Operation != Operation.ExtractAudio) throw new InvalidOperationException("No usable video stream was detected.");
        if (!double.IsFinite(o.Start) || !double.IsFinite(o.End) || !double.IsFinite(o.Fps) || o.Fps < 0 || o.Fps > 240 || o.Height < 0 || o.Height > 4320 || o.VideoKbps < 0 || o.VideoKbps > 200000)
            throw new InvalidOperationException("Resolution, frame rate, bitrate or timestamps are outside the supported range.");
        var ext = o.Operation == Operation.ExtractAudio ? o.AudioFormat : o.Operation == Operation.ExtractFrame ? "png" : o.Container.ToLowerInvariant();
        if (!(new[] { "mp4", "mkv", "mov", "avi", "webm", "flv", "ts", "mp3", "m4a", "wav", "flac", "png" }).Contains(ext)) throw new InvalidOperationException("Unsupported output container.");
        var input = new List<string>();
        var output = new List<string>();
        var duration = m.Duration;
        var copy = o.Operation is Operation.Remux or Operation.Repair || o.Operation == Operation.Cut && !o.Precise ||
            o.Operation == Operation.Convert && o.PreferCopy && o.Height == 0 && o.Fps == 0 && o.VideoKbps == 0 && o.Codec == m.Video?.Codec;
        var all = m.Streams.Where(s => s.Type is "video" or "audio" or "subtitle").Where(s => !s.AttachedPicture).ToList();
        var audio = o.RemoveAudio ? [] : all.Where(s => s.Type == "audio" && (o.AudioTracks == null || o.AudioTracks.Contains(s.Index))).ToList();
        var subs = o.RemoveSubtitles ? [] : all.Where(s => s.Type == "subtitle" && (o.SubtitleTracks == null || o.SubtitleTracks.Contains(s.Index))).ToList();
        if (o.AudioTracks?.Any(i => !m.Streams.Any(s => s.Type == "audio" && s.Index == i)) == true || o.SubtitleTracks?.Any(i => !m.Streams.Any(s => s.Type == "subtitle" && s.Index == i)) == true)
            throw new InvalidOperationException("A selected stream index does not exist in this file. Use global stream IDs from Media information.");
        if (o.Operation == Operation.Repair && o.RepairMode != "Rebuild container") input.AddRange(["-fflags", "+genpts"]);
        if (o.Operation == Operation.Cut)
        {
            if (m.Duration <= 0) throw new InvalidOperationException("A known duration is required for cutting. Try repairing the container first.");
            if (o.Start < 0 || o.End <= o.Start || o.End > m.Duration + .05) throw new InvalidOperationException("Choose an end after the start and within the video duration.");
            duration = o.End - o.Start;
            if (!o.Precise) input.AddRange(["-ss", Format.Number(o.Start)]);
        }
        if (o.Operation == Operation.ExtractFrame)
        {
            if (o.Start < 0 || m.Duration > 0 && o.Start >= m.Duration) throw new InvalidOperationException("Choose a frame timestamp within the video.");
            input.AddRange(["-ss", Format.Number(o.Start)]);
            output.AddRange(["-map", $"0:{m.Video!.Index}", "-frames:v", "1", "-update", "1"]);
            return new(ext, input, output, false, .1, null, "Decode and export one frame as a PNG image.", "", false);
        }
        if (o.Operation == Operation.ExtractAudio)
        {
            if (audio.Count == 0) throw new InvalidOperationException("No selected audio stream is available.");
            output.AddRange(["-map", $"0:{audio[0].Index}", "-vn", "-sn", "-c:a", ext switch { "mp3" => "libmp3lame", "m4a" => "aac", "wav" => "pcm_s16le", "flac" => "flac", _ => throw new InvalidOperationException("Choose MP3, M4A, WAV or FLAC for audio extraction.") }]);
            if (ext is "mp3" or "m4a") output.AddRange(["-b:a", "160k"]);
            return new(ext, input, output, false, duration, null, "Extract the first selected audio track. Audio is re-encoded for predictable compatibility.", "", false);
        }
        var target = Target(m, o);
        if (target.HasValue && (target <= 0 || o.Compression == CompressionMode.Reduction && (o.Reduction < 10 || o.Reduction > 90))) throw new InvalidOperationException("Choose a valid target size or a reduction from 10% to 90%.");
        if (target.HasValue && o.Codec != "h264") throw new InvalidOperationException("Size targeting uses two-pass CPU H.264. Choose H.264 or use quality-based compression for another codec.");
        if (copy)
        {
            var selected = all.Where(s => s.Type == "video").Concat(audio).Concat(subs).ToList();
            var incompatible = selected.Where(s => !Compatible(ext, s)).ToList();
            if (incompatible.Count > 0)
            {
                if (o.Operation == Operation.Convert) copy = false;
                else throw new InvalidOperationException($"Cannot copy to {ext.ToUpperInvariant()}: " + string.Join(", ", incompatible.Select(s => $"stream {s.Index} ({s.Codec})")) + ". Choose MKV, remove incompatible tracks, or use Converter to re-encode.");
            }
        }
        if (o.Operation == Operation.Cut && o.Precise) output.AddRange(["-ss", Format.Number(o.Start)]);
        if (o.Operation == Operation.Cut) output.AddRange(["-t", Format.Number(duration)]);
        var videoStreams = copy ? all.Where(s => s.Type == "video").ToList() : [m.Video!];
        foreach (var v in videoStreams) output.AddRange(["-map", $"0:{v.Index}"]);
        foreach (var a in audio) output.AddRange(["-map", $"0:{a.Index}"]);
        foreach (var s in subs) output.AddRange(["-map", $"0:{s.Index}"]);
        output.AddRange(["-map_metadata", "0", "-map_chapters", o.Operation == Operation.Cut ? "-1" : "0"]);
        var warning = "";
        var gpu = !copy && o.Codec == "h264" && !target.HasValue && o.Acceleration != Acceleration.CPU && qsvAvailable && !forceCpu;
        if (o.Acceleration == Acceleration.GPU && !gpu && !copy) warning = "GPU encoding is unavailable for this operation. Using CPU. ";
        if (copy) output.AddRange(["-c", "copy"]);
        else
        {
            if (!Compatible(ext, m.Video! with { Codec = o.Codec })) throw new InvalidOperationException($"{o.Codec} is incompatible with {ext.ToUpperInvariant()}. Choose a different codec or container.");
            var encoder = o.Codec switch { "h264" => gpu ? "h264_qsv" : "libx264", "hevc" => "libx265", "vp9" => "libvpx-vp9", "mpeg4" => "mpeg4", _ => throw new InvalidOperationException("Select H.264, H.265, VP9 or MPEG-4.") };
            output.AddRange(["-c:v", encoder, "-threads", Math.Clamp(o.Threads, 1, 4).ToString()]);
            var crf = o.Preset switch { "Fast" => 25, "High Quality" => 20, "Small File" => 28, "Balanced" => o.Operation == Operation.Compress ? 24 : 23, _ => Math.Clamp(o.Crf, 0, 51) };
            var speed = o.Preset switch { "Fast" => "ultrafast", "High Quality" => "fast", "Custom" => o.Speed, _ => "veryfast" };
            if (!(new[] { "ultrafast", "superfast", "veryfast", "faster", "fast", "medium", "slow" }).Contains(speed)) throw new InvalidOperationException("Unsupported CPU speed preset.");
            if (o.Codec is "h264" or "hevc") output.AddRange(["-preset", gpu ? "veryfast" : speed]);
            if (o.Codec == "hevc") output.AddRange(["-x265-params", $"pools={Math.Clamp(o.Threads, 1, 4)}:frame-threads=1"]);
            if (o.Codec == "vp9") output.AddRange(["-deadline", "good", "-cpu-used", "4", "-row-mt", "1"]);
            var audioKbps = audio.Sum(a => a.Channels == 1 ? 96 : 128);
            if (target.HasValue)
            {
                if (duration <= 0) throw new InvalidOperationException("Size targeting requires a known duration. Try rebuilding the container first.");
                var kbps = TargetVideoKbps(target.Value, duration, audioKbps);
                if (kbps < 64) throw new InvalidOperationException("This target leaves less than 64 kb/s for video after audio. Increase the target size or remove audio tracks.");
                if (kbps < 500 || target.Value < m.Size * .15) warning += "This target may cause significant quality loss. Reduce resolution or choose a larger size. ";
                output.AddRange(["-b:v", $"{kbps}k"]);
            }
            else if (o.VideoKbps > 0) output.AddRange(["-b:v", $"{o.VideoKbps}k"]);
            else if (gpu) output.AddRange(["-global_quality", crf.ToString()]);
            else if (o.Codec == "mpeg4") output.AddRange(["-q:v", Math.Clamp(crf / 3, 2, 15).ToString()]);
            else { output.AddRange(["-crf", crf.ToString()]); if (o.Codec == "vp9") output.AddRange(["-b:v", "0"]); }
            output.AddRange(["-pix_fmt", "yuv420p"]);
            var filters = new List<string>();
            if (o.Operation == Operation.Transform)
            {
                var transform = o.Transform switch { "Rotate 90°" => "transpose=1", "Rotate 180°" => "hflip,vflip", "Rotate 270°" => "transpose=2", "Flip horizontal" => "hflip", "Flip vertical" => "vflip", _ => "" };
                if (transform != "") filters.Add(transform);
                if (o.Crop != "")
                {
                    if (!System.Text.RegularExpressions.Regex.IsMatch(o.Crop, @"^\d{1,5}:\d{1,5}:\d{1,5}:\d{1,5}$")) throw new InvalidOperationException("Crop must be width:height:x:y using non-negative integers.");
                    var crop = o.Crop.Split(':').Select(int.Parse).ToArray();
                    if (crop[0] < 2 || crop[1] < 2 || crop[0] % 2 != 0 || crop[1] % 2 != 0 || crop[0] + crop[2] > m.Video!.Width || crop[1] + crop[3] > m.Video.Height) throw new InvalidOperationException("Crop must have even dimensions and fit inside the source frame (before rotation).");
                    filters.Insert(0, "crop=" + o.Crop);
                }
            }
            if (o.Height > 0) filters.Add($"scale=-2:{o.Height}");
            else filters.Add("scale=trunc(iw/2)*2:trunc(ih/2)*2");
            if (filters.Count > 0) output.AddRange(["-vf", string.Join(',', filters)]);
            if (o.Fps > 0) output.AddRange(["-r", Format.Number(o.Fps)]);
            if (audio.Count > 0)
            {
                output.AddRange(["-c:a", ext == "webm" ? "libopus" : ext == "avi" ? "libmp3lame" : "aac"]);
                for (int i = 0; i < audio.Count; i++) output.AddRange([$"-b:a:{i}", audio[i].Channels == 1 ? "96k" : "128k", $"-ac:a:{i}", Math.Min(2, Math.Max(1, audio[i].Channels)).ToString()]);
                if (audio.Any(a => a.Channels > 2)) warning += "Encoded audio is downmixed to stereo. Use remux to preserve multichannel audio. ";
            }
            if (subs.Count > 0)
            {
                var text = new[] { "subrip", "ass", "ssa", "mov_text", "webvtt", "text" };
                for (int i = 0; i < subs.Count; i++)
                {
                    var codec = Compatible(ext, subs[i]) ? "copy" : text.Contains(subs[i].Codec) && ext is "mp4" or "mov" ? "mov_text" : text.Contains(subs[i].Codec) && ext == "mkv" ? "srt" : text.Contains(subs[i].Codec) && ext == "webm" ? "webvtt" : "";
                    if (codec == "") throw new InvalidOperationException($"Subtitle stream {subs[i].Index} ({subs[i].Codec}) is incompatible. Remove it or choose MKV.");
                    output.AddRange([$"-c:s:{i}", codec]);
                    if (codec != "copy") warning += "Subtitle format conversion may simplify styling. ";
                }
            }
        }
        if (o.Operation == Operation.Repair) output.AddRange(["-avoid_negative_ts", "make_zero"]);
        if (o.Operation == Operation.Cut && !o.Precise) output.AddRange(["-avoid_negative_ts", "make_zero"]);
        if (ext is "mp4" or "mov") output.AddRange(["-movflags", "+faststart"]);
        var explanation = copy ? "Stream copy: original encoded video quality is preserved. Compatible video, audio and subtitle tracks are retained." : "Re-encoding: required by codec, quality, size, precise cut or image settings. Video is encoded; selected audio is encoded to stereo/mono.";
        if (copy && o.Operation == Operation.Cut) warning += "Fast cuts start at a nearby keyframe and may include extra frames. Use Precise for accurate boundaries. ";
        if (target.HasValue) { explanation += " Two-pass CPU H.264 targets the calculated bitrate, allowing 3% for container overhead."; warning += "Exact sizes and reductions are not guaranteed, especially for very short clips or subtitle-heavy files."; }
        return new(ext, input, output, target.HasValue, duration, target, explanation, warning.Trim(), gpu);
    }
}
