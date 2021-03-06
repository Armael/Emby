﻿using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.LiveTv;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Emby.XmlTv.Classes;
using Emby.XmlTv.Entities;
using MediaBrowser.Common.Extensions;
using MediaBrowser.Common.Net;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Model.IO;
using MediaBrowser.Model.Logging;

namespace Emby.Server.Implementations.LiveTv.Listings
{
    public class XmlTvListingsProvider : IListingsProvider
    {
        private readonly IServerConfigurationManager _config;
        private readonly IHttpClient _httpClient;
        private readonly ILogger _logger;
        private readonly IFileSystem _fileSystem;

        public XmlTvListingsProvider(IServerConfigurationManager config, IHttpClient httpClient, ILogger logger, IFileSystem fileSystem)
        {
            _config = config;
            _httpClient = httpClient;
            _logger = logger;
            _fileSystem = fileSystem;
        }

        public string Name
        {
            get { return "XmlTV"; }
        }

        public string Type
        {
            get { return "xmltv"; }
        }

        private string GetLanguage()
        {
            return _config.Configuration.PreferredMetadataLanguage;
        }

        private async Task<string> GetXml(string path, CancellationToken cancellationToken)
        {
            _logger.Info("xmltv path: {0}", path);

            if (!path.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            {
                return path;
            }

            var cacheFilename = DateTime.UtcNow.DayOfYear.ToString(CultureInfo.InvariantCulture) + "-" + DateTime.UtcNow.Hour.ToString(CultureInfo.InvariantCulture) + ".xml";
            var cacheFile = Path.Combine(_config.ApplicationPaths.CachePath, "xmltv", cacheFilename);
            if (_fileSystem.FileExists(cacheFile))
            {
                return cacheFile;
            }

            _logger.Info("Downloading xmltv listings from {0}", path);

            var tempFile = await _httpClient.GetTempFile(new HttpRequestOptions
            {
                CancellationToken = cancellationToken,
                Url = path,
                Progress = new Progress<Double>(),
                DecompressionMethod = CompressionMethod.Gzip,

                // It's going to come back gzipped regardless of this value
                // So we need to make sure the decompression method is set to gzip
                EnableHttpCompression = true,

                UserAgent = "Emby/3.0"

            }).ConfigureAwait(false);

            _fileSystem.CreateDirectory(Path.GetDirectoryName(cacheFile));

            using (var stream = _fileSystem.OpenRead(tempFile))
            {
                using (var reader = new StreamReader(stream, Encoding.UTF8))
                {
                    using (var fileStream = _fileSystem.GetFileStream(cacheFile, FileOpenMode.Create, FileAccessMode.Write, FileShareMode.Read))
                    {
                        using (var writer = new StreamWriter(fileStream))
                        {
                            while (!reader.EndOfStream)
                            {
                                writer.WriteLine(reader.ReadLine());
                            }
                        }
                    }
                }
            }

            _logger.Debug("Returning xmltv path {0}", cacheFile);
            return cacheFile;
        }

        public async Task<IEnumerable<ProgramInfo>> GetProgramsAsync(ListingsProviderInfo info, string channelNumber, string channelName, DateTime startDateUtc, DateTime endDateUtc, CancellationToken cancellationToken)
        {
            if (!await EmbyTV.EmbyTVRegistration.Instance.EnableXmlTv().ConfigureAwait(false))
            {
                var length = endDateUtc - startDateUtc;
                if (length.TotalDays > 1)
                {
                    endDateUtc = startDateUtc.AddDays(1);
                }
            }

            var path = await GetXml(info.Path, cancellationToken).ConfigureAwait(false);
            var reader = new XmlTvReader(path, GetLanguage());

            var results = reader.GetProgrammes(channelNumber, startDateUtc, endDateUtc, cancellationToken);
            return results.Select(p => GetProgramInfo(p, info));
        }

        private ProgramInfo GetProgramInfo(XmlTvProgram p, ListingsProviderInfo info)
        {
            var episodeTitle = p.Episode == null ? null : p.Episode.Title;

            var programInfo = new ProgramInfo
            {
                ChannelId = p.ChannelId,
                EndDate = GetDate(p.EndDate),
                EpisodeNumber = p.Episode == null ? null : p.Episode.Episode,
                EpisodeTitle = episodeTitle,
                Genres = p.Categories,
                Id = String.Format("{0}_{1:O}", p.ChannelId, p.StartDate), // Construct an id from the channel and start date,
                StartDate = GetDate(p.StartDate),
                Name = p.Title,
                Overview = p.Description,
                ShortOverview = p.Description,
                ProductionYear = !p.CopyrightDate.HasValue ? (int?)null : p.CopyrightDate.Value.Year,
                SeasonNumber = p.Episode == null ? null : p.Episode.Series,
                IsSeries = p.Episode != null,
                IsRepeat = p.IsPreviouslyShown && !p.IsNew,
                IsPremiere = p.Premiere != null,
                IsKids = p.Categories.Any(c => info.KidsCategories.Contains(c, StringComparer.OrdinalIgnoreCase)),
                IsMovie = p.Categories.Any(c => info.MovieCategories.Contains(c, StringComparer.OrdinalIgnoreCase)),
                IsNews = p.Categories.Any(c => info.NewsCategories.Contains(c, StringComparer.OrdinalIgnoreCase)),
                IsSports = p.Categories.Any(c => info.SportsCategories.Contains(c, StringComparer.OrdinalIgnoreCase)),
                ImageUrl = p.Icon != null && !String.IsNullOrEmpty(p.Icon.Source) ? p.Icon.Source : null,
                HasImage = p.Icon != null && !String.IsNullOrEmpty(p.Icon.Source),
                OfficialRating = p.Rating != null && !String.IsNullOrEmpty(p.Rating.Value) ? p.Rating.Value : null,
                CommunityRating = p.StarRating.HasValue ? p.StarRating.Value : (float?)null,
                SeriesId = p.Episode != null ? p.Title.GetMD5().ToString("N") : null,
                ShowId = ((p.Title ?? string.Empty) + (episodeTitle ?? string.Empty)).GetMD5().ToString("N")
            };

            if (programInfo.IsMovie)
            {
                programInfo.IsSeries = false;
                programInfo.EpisodeNumber = null;
                programInfo.EpisodeTitle = null;
            }

            return programInfo;
        }

        private DateTime GetDate(DateTime date)
        {
            if (date.Kind != DateTimeKind.Utc)
            {
                date = DateTime.SpecifyKind(date, DateTimeKind.Utc);
            }
            return date;
        }

        public async Task AddMetadata(ListingsProviderInfo info, List<ChannelInfo> channels, CancellationToken cancellationToken)
        {
            // Add the channel image url
            var path = await GetXml(info.Path, cancellationToken).ConfigureAwait(false);
            var reader = new XmlTvReader(path, GetLanguage());
            var results = reader.GetChannels().ToList();

            if (channels != null)
            {
                foreach (var c in channels)
                {
                    var channelNumber = info.GetMappedChannel(c.Number);
                    var match = results.FirstOrDefault(r => string.Equals(r.Id, channelNumber, StringComparison.OrdinalIgnoreCase));

                    if (match != null && match.Icon != null && !String.IsNullOrEmpty(match.Icon.Source))
                    {
                        c.ImageUrl = match.Icon.Source;
                    }
                }
            }
        }

        public Task Validate(ListingsProviderInfo info, bool validateLogin, bool validateListings)
        {
            // Assume all urls are valid. check files for existence
            if (!info.Path.StartsWith("http", StringComparison.OrdinalIgnoreCase) && !_fileSystem.FileExists(info.Path))
            {
                throw new FileNotFoundException("Could not find the XmlTv file specified:", info.Path);
            }

            return Task.FromResult(true);
        }

        public async Task<List<NameIdPair>> GetLineups(ListingsProviderInfo info, string country, string location)
        {
            // In theory this should never be called because there is always only one lineup
            var path = await GetXml(info.Path, CancellationToken.None).ConfigureAwait(false);
            var reader = new XmlTvReader(path, GetLanguage());
            var results = reader.GetChannels();

            // Should this method be async?
            return results.Select(c => new NameIdPair() { Id = c.Id, Name = c.DisplayName }).ToList();
        }

        public async Task<List<ChannelInfo>> GetChannels(ListingsProviderInfo info, CancellationToken cancellationToken)
        {
            // In theory this should never be called because there is always only one lineup
            var path = await GetXml(info.Path, cancellationToken).ConfigureAwait(false);
            var reader = new XmlTvReader(path, GetLanguage());
            var results = reader.GetChannels();

            // Should this method be async?
            return results.Select(c => new ChannelInfo()
            {
                Id = c.Id,
                Name = c.DisplayName,
                ImageUrl = c.Icon != null && !String.IsNullOrEmpty(c.Icon.Source) ? c.Icon.Source : null,
                Number = c.Id

            }).ToList();
        }
    }
}