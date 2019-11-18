﻿using System;
using System.Collections.Concurrent;
using System.IO.Compression;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using SpleeterAPI.Youtube;

namespace SpleeterAPI.Controllers
{
    [Route("yt")]
    [ApiController]
    public class YoutubeController : ControllerBase
    {
        private readonly ILogger<YoutubeController> _logger;
        private readonly static ConcurrentDictionary<string, DateTime> _processing = new ConcurrentDictionary<string, DateTime>();

        public YoutubeController(ILogger<YoutubeController> logger)
        {
            _logger = logger;
        }

        /// <summary>
        /// Process a youtube video to split the audio 
        /// </summary>
        /// <param name="vid">Youtube video ID</param>
        /// <param name="format">2stems, 4stems or 5stems</param>
        [HttpGet("p/{format}/{vid}")]
        [Produces("application/json")]
        public ActionResult<YoutubeProcessResponse> Process([FromRoute] string format, [FromRoute] string vid, [FromQuery] bool includeOriginalAudio = false, [FromQuery] bool hf = false)
        {
            if (format != "2stems" && format != "4stems" && format != "5stems" && format != "karaoke")
            {
                return BadRequest("Format must be '2stems', '4stems' or '5stems'");
            }
            var fileId = GetFileId(format, vid, hf);
            var now = DateTime.UtcNow;

            if (_processing.TryGetValue(fileId, out DateTime startDate))
            {
                var startedSecondsAgo = (now - startDate).TotalSeconds;
                if (startedSecondsAgo < 600)
                {
                    return new YoutubeProcessResponse() { Error = $"File {fileId} is being processed, started {startedSecondsAgo:N0} seconds ago. Try again later in few more minutes..." };
                }
            }

            if (format == "karaoke")
            {
                var mp3File = $"/output/{fileId}.mp3";
                if (System.IO.File.Exists(mp3File))
                {
                    return new YoutubeProcessResponse() { FileId = fileId };
                }
            }
            var zipFile = $"/output/{fileId}.zip";
            if (System.IO.File.Exists(zipFile))
            {
                return new YoutubeProcessResponse() { FileId = fileId };
            }

            _processing[fileId] = now;

            // Download audio from youtube vid
            var audioData = YoutubeHelper.DownloadAudio(vid, fileId);

            // Separate audio stems
            var separateResult = SpliterHelper.Split(audioData.AudioFileFullPath, fileId, format, hf);

            if (separateResult.ExitCode != 0)
            {
                _processing.TryRemove(fileId, out _);
                return Problem($"spleeter separate command exited with code {separateResult.ExitCode}\nException: {separateResult.Exception}\nMessages: {separateResult.Output}.");
            }

            if (format != "karaoke" && includeOriginalAudio)
            {
                System.IO.File.Copy(audioData.AudioFileFullPath, $"/output/{fileId}/original.webm");
            }

            if (format == "karaoke")
            {
                System.IO.File.Copy($"/output/{fileId}/download.audio/accompaniment.mp3", $"/output/{fileId}.mp3");
            }
            else
            {
                // Zip stems
                ZipFile.CreateFromDirectory($"/output/{fileId}", zipFile, CompressionLevel.Fastest, false);
            }

            // Delete temp files
            System.IO.File.Delete(audioData.AudioFileFullPath);
            System.IO.Directory.Delete($"/output/{fileId}", true);

            _processing.TryRemove(fileId, out _);

            return new YoutubeProcessResponse() { FileId = fileId };
        }

        /// <summary>
        /// Downloads an already processed youtube video
        /// </summary>
        /// <param name="vid">Youtube video ID</param>
        /// <param name="format">2stems, 4stems or 5stems</param>
        [HttpGet("d/{format}/{vid}")]
        public ActionResult Download([FromRoute] string format, [FromRoute] string vid, [FromQuery] bool hf = false)
        {
            var fileId = GetFileId(format, vid, hf);
            if (format == "karaoke")
            {
                var mp3File = $"/output/{fileId}.mp3";
                if (System.IO.File.Exists(mp3File))
                {
                    return PhysicalFile(mp3File, "audio/mpeg", $"{fileId}.mp3");
                }
            }
            var zipFile = $"/output/{fileId}.zip";
            if (System.IO.File.Exists(zipFile))
            {
                return PhysicalFile(zipFile, "application/zip", $"{fileId}.zip");
            }
            return Problem($"File {zipFile} not found");
        }

        /// <summary>
        /// Query the status of a youtube video being splitted
        /// </summary>
        /// <param name="vid">Youtube video ID</param>
        /// <param name="format">2stems, 4stems or 5stems</param>
        [HttpGet("q/{format}/{vid}")]
        [Produces("application/json")]
        public ActionResult<YoutubeStatusResponse> Query([FromRoute] string format, [FromRoute] string vid, [FromQuery] bool hf = false)
        {
            var fileId = GetFileId(format, vid, hf);
            var result = new YoutubeStatusResponse() { FileId = fileId };
            if (_processing.TryGetValue(fileId, out DateTime startDate))
            {
                result.StartDate = startDate;
            }
            if (format == "karaoke")
            {
                var mp3File = $"/output/{fileId}.mp3";
                if (System.IO.File.Exists(mp3File))
                {
                    result.Status = "ReadyToDownload";
                }
                else
                {
                    result.Status = "Unknown";
                }
            }
            else
            {
                var zipFile = $"/output/{fileId}.zip";
                if (System.IO.File.Exists(zipFile))
                {
                    result.Status = "ReadyToDownload";
                }
                else if (YoutubeHelper.AudioExists(vid))
                {
                    result.Status = "Splitting";
                }
                else
                {
                    result.Status = "Unknown";
                }
            }
            return result;
        }

        private string GetFileId(string format, string vid, bool includeHighFreq)
        {
            var fileId = $"{vid}.{format}";
            if (includeHighFreq)
            {
                fileId += ".hf";
            }
            return fileId;
        }
    }
}