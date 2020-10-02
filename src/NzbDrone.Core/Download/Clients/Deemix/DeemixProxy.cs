using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Newtonsoft.Json.Linq;
using NLog;
using NzbDrone.Common.Disk;
using NzbDrone.Common.Extensions;
using NzbDrone.Common.Serializer;
using SocketIOClient;

namespace NzbDrone.Core.Download.Clients.Deemix
{
    public class DeemixClientItem : DownloadClientItem
    {
        public DateTime Started { get; set; }
    }

    public class DeemixProxy : IDisposable
    {
        private static int AddId;
        private static Dictionary<string, long> Bitrates = new Dictionary<string, long>
        {
            { "1", 128 },
            { "3", 320 },
            { "9", 1000 }
        };
        private static Dictionary<string, string> Formats = new Dictionary<string, string>
        {
            { "1", "MP3 128" },
            { "3", "MP3 320" },
            { "9", "FLAC" }
        };

        private readonly Logger _logger;
        private readonly ManualResetEventSlim _connected;
        private readonly Dictionary<int, DeemixPendingItem<string>> _pendingAdds;
        private readonly Dictionary<int, DeemixPendingItem<DeemixSearchResponse>> _pendingSearches;

        private bool _disposed;
        private SocketIO _client;
        private List<DeemixClientItem> _queue;
        private DeemixConfig _config;

        public DeemixProxy(string url,
                           Logger logger)
        {
            _logger = logger;

            _connected = new ManualResetEventSlim(false);
            _queue = new List<DeemixClientItem>();

            _pendingAdds = new Dictionary<int, DeemixPendingItem<string>>();
            _pendingSearches = new Dictionary<int, DeemixPendingItem<DeemixSearchResponse>>();

            Connect(url);
        }

        public DeemixConfig GetSettings()
        {
            if (!_connected.Wait(5000))
            {
                throw new DownloadClientUnavailableException("Deemix not connected");
            }

            return _config;
        }

        public List<DeemixClientItem> GetQueue()
        {
            if (!_connected.Wait(5000))
            {
                throw new DownloadClientUnavailableException("Deemix not connected");
            }

            return _queue;
        }

        public void RemoveFromQueue(string downloadId)
        {
            _client.EmitAsync("removeFromQueue", downloadId);
        }

        public string Download(string url, int bitrate)
        {
            if (!_connected.Wait(5000))
            {
                throw new DownloadClientUnavailableException("Deemix not connected");
            }

            _logger.Trace($"Downloading {url} bitrate {bitrate}");

            using (var pending = new DeemixPendingItem<string>())
            {
                var ack = Interlocked.Increment(ref AddId);
                _pendingAdds[ack] = pending;

                _client.EmitAsync("addToQueue",
                                  new
                                  {
                                      url,
                                      bitrate,
                                      ack
                                  });

                _logger.Trace($"Awaiting result for add {ack}");
                var added = pending.Wait(60000);

                _pendingAdds.Remove(ack);

                if (!added)
                {
                    throw new DownloadClientUnavailableException("Could not add item");
                }

                return pending.Item;
            }
        }

        public DeemixSearchResponse Search(string term, int count, int offset)
        {
            if (!_connected.Wait(5000))
            {
                throw new DownloadClientUnavailableException("Deemix not connected");
            }

            _logger.Trace($"Searching for {term}");

            using (var pending = new DeemixPendingItem<DeemixSearchResponse>())
            {
                var ack = Interlocked.Increment(ref AddId);
                _pendingSearches[ack] = pending;

                _client.EmitAsync("albumSearch",
                                  new
                                  {
                                      term,
                                      start = offset,
                                      nb = count,
                                      ack
                                  });

                _logger.Trace($"Awaiting result for search {ack}");
                var gotResult = pending.Wait(60000);

                _pendingSearches.Remove(ack);

                if (!gotResult)
                {
                    throw new DownloadClientUnavailableException("Could not search for {0}", term);
                }

                return pending.Item;
            }
        }

        public DeemixSearchResponse RecentReleases()
        {
            if (!_connected.Wait(5000))
            {
                throw new DownloadClientUnavailableException("Deemix not connected");
            }

            _logger.Trace("Getting recent releases");

            using (var pending = new DeemixPendingItem<DeemixSearchResponse>())
            {
                var ack = Interlocked.Increment(ref AddId);
                _pendingSearches[ack] = pending;

                _client.EmitAsync("newReleases",
                                  new
                                  {
                                      ack
                                  });

                _logger.Trace($"Awaiting result for RSS {ack}");
                var gotResult = pending.Wait(60000);

                _pendingSearches.Remove(ack);

                if (!gotResult)
                {
                    throw new DownloadClientUnavailableException("Could not get recent releases");
                }

                return pending.Item;
            }
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (_disposed)
            {
                return;
            }

            if (disposing)
            {
                // Dispose managed state (managed objects).
                Disconnect();
                _connected.Dispose();
            }

            _disposed = true;
        }

        private void Disconnect()
        {
            if (_client != null)
            {
                _logger.Trace("Disconnecting");
                _client.DisconnectAsync().GetAwaiter().GetResult();
            }
        }

        private void Connect(string url)
        {
            _queue = new List<DeemixClientItem>();

            var options = new SocketIOOptions
            {
                ReconnectionDelay = 1000,
                ReconnectionDelayMax = 10000
            };

            _client = new SocketIO(url, options);
            _client.OnConnected += (s, e) =>
            {
                _logger.Trace("connected");
                _connected.Set();
            };

            _client.OnDisconnected += (s, e) =>
            {
                _logger.Trace("disconnected");
                _connected.Reset();
            };

            _client.OnPing += (s, e) => _logger.Trace("ping");
            _client.OnPong += (s, e) => _logger.Trace("pong");

            _client.OnReconnecting += (s, e) => _logger.Trace("reconnecting");
            _client.OnError += (s, e) => _logger.Warn($"error {e}");

            _client.On("init_settings", ErrorHandler(OnInitSettings));
            _client.On("updateSettings", ErrorHandler(OnUpdateSettings));
            _client.On("init_downloadQueue", ErrorHandler(OnInitQueue));
            _client.On("addedToQueue", ErrorHandler(OnAddedToQueue));
            _client.On("updateQueue", ErrorHandler(OnUpdateQueue));
            _client.On("finishDownload", ErrorHandler(response => UpdateDownloadStatus(response, DownloadItemStatus.Completed)));
            _client.On("startDownload", ErrorHandler(response => UpdateDownloadStatus(response, DownloadItemStatus.Downloading)));
            _client.On("removedFromQueue", ErrorHandler(OnRemovedFromQueue));
            _client.On("removedAllDownloads", ErrorHandler(OnRemovedAllFromQueue));
            _client.On("removedFinishedDownloads", ErrorHandler(OnRemovedFinishedFromQueue));
            _client.On("loginNeededToDownload", OnLoginRequired);
            _client.On("queueError", OnQueueError);
            _client.On("albumSearch", ErrorHandler(OnSearchResult));
            _client.On("newReleases", ErrorHandler(OnSearchResult));

            _logger.Trace("Connecting to deemix");
            _connected.Reset();
            _client.ConnectAsync();

            _logger.Trace("waiting for connection");
            if (!_connected.Wait(60000))
            {
                throw new DownloadClientUnavailableException("Unable to connect to Deemix");
            }
        }

        private Action<SocketIOResponse> ErrorHandler(Action<SocketIOResponse> action)
        {
            return (SocketIOResponse x) =>
            {
                try
                {
                    action(x);
                }
                catch (Exception e)
                {
                    e.Data.Add("response", x.GetValue().ToString());
                    _logger.Error(e, "Deemix error");
                }
            };
        }

        private void OnInitSettings(SocketIOResponse response)
        {
            _logger.Trace("got settings");
            _config = response.GetValue<DeemixConfig>();
            _logger.Trace($"Download path: {_config.DownloadLocation}");
        }

        private void OnUpdateSettings(SocketIOResponse response)
        {
            _logger.Trace("got settings update");
            _config = response.GetValue<DeemixConfig>(0);
        }

        private void OnInitQueue(SocketIOResponse response)
        {
            _logger.Trace("got queue");
            var dq = response.GetValue<DeemixQueue>();

            var items = dq.QueueList.Values.ToList();
            _logger.Trace($"sent {items.Count} items");

            _queue = items.Select(x => ToDownloadClientItem(x)).ToList();
            _logger.Trace($"queue length {_queue.Count}");
            _logger.Trace(_queue.ConcatToString(x => x.Title, "\n"));
        }

        private void OnUpdateQueue(SocketIOResponse response)
        {
            _logger.Trace("Got update queue");
            var item = response.GetValue<DeemixQueueUpdate>();

            var queueItem = _queue.SingleOrDefault(x => x.DownloadId == item.Uuid);
            if (queueItem == null)
            {
                return;
            }

            if (item.Progress.HasValue)
            {
                var progress = (double)item.Progress.Value / 100;
                queueItem.RemainingSize = (long)((1 - progress) * queueItem.TotalSize);

                var elapsed = DateTime.UtcNow - queueItem.Started;
                queueItem.RemainingTime = TimeSpan.FromTicks((long)(elapsed.Ticks * (1 - progress) / progress));
            }

            if (item.ExtrasPath.IsNotNullOrWhiteSpace())
            {
                queueItem.OutputPath = new OsPath(item.ExtrasPath);
            }
        }

        private void UpdateDownloadStatus(SocketIOResponse response, DownloadItemStatus status)
        {
            _logger.Trace($"Got update download status {status}");
            var uuid = response.GetValue<string>();

            var queueItem = _queue.SingleOrDefault(x => x.DownloadId == uuid);
            if (queueItem != null)
            {
                queueItem.Status = status;
            }
        }

        private void OnRemovedFromQueue(SocketIOResponse response)
        {
            _logger.Trace("Got removed from queue");
            var uuid = response.GetValue<string>();

            var queueItem = _queue.SingleOrDefault(x => x.DownloadId == uuid);
            if (queueItem != null)
            {
                _queue.Remove(queueItem);
            }
        }

        private void OnRemovedAllFromQueue(SocketIOResponse response)
        {
            _logger.Trace("Got removed all from queue");
            _queue = new List<DeemixClientItem>();
        }

        private void OnRemovedFinishedFromQueue(SocketIOResponse response)
        {
            _logger.Trace("Got removed all from queue");
            _queue = _queue.Where(x => x.Status != DownloadItemStatus.Completed).ToList();
        }

        private void OnAddedToQueue(SocketIOResponse response)
        {
            _logger.Trace("Got added to queue");

            if (response.GetValue().Type != JTokenType.Object)
            {
                _logger.Trace("New queue item not an object, skipping");
                return;
            }

            var item = response.GetValue<DeemixQueueItem>();

            if (item.Type != "album")
            {
                _logger.Trace("New queue item not an album, skipping");
                return;
            }

            _logger.Trace($"got ack {item.Ack}");

            if (item.Ack.HasValue && _pendingAdds.TryGetValue(item.Ack.Value, out var pending))
            {
                pending.Item = item.Uuid;
                pending.Ack();
            }

            var dci = ToDownloadClientItem(item);
            dci.Started = DateTime.UtcNow;
            _queue.Add(dci);
        }

        private void OnQueueError(SocketIOResponse response)
        {
            var error = response.GetValue().ToString();
            _logger.Error($"Queue error:\n {error}");
        }

        private void OnSearchResult(SocketIOResponse response)
        {
            _logger.Trace("Got search response");
            var result = response.GetValue<DeemixSearchResponse>();
            _logger.Trace($"got ack {result.Ack}");

            if (result.Ack.HasValue && _pendingSearches.TryGetValue(result.Ack.Value, out var pending))
            {
                pending.Item = result;
                pending.Ack();
            }
        }

        private void OnLoginRequired(SocketIOResponse response)
        {
            throw new DownloadClientUnavailableException("login required");
        }

        private static DeemixClientItem ToDownloadClientItem(DeemixQueueItem x)
        {
            var title = $"{x.Artist} - {x.Title} [WEB] {Formats[x.Bitrate]}";
            if (x.Explicit)
            {
                title += " [Explicit]";
            }

            // assume 3 mins per track, bitrates in kbps
            var size = (long)x.Size * 180L * Bitrates[x.Bitrate] * 128L;

            var item = new DeemixClientItem
            {
                DownloadId = x.Uuid,
                Title = title,
                TotalSize = size,
                RemainingSize = (long)(1 - (((double)x.Progress) / 100)) * x.Size,
                Status = GetItemStatus(x),
                CanMoveFiles = true,
                CanBeRemoved = true
            };

            if (x.ExtrasPath.IsNotNullOrWhiteSpace())
            {
                item.OutputPath = new OsPath(x.ExtrasPath);
            }

            return item;
        }

        private static DownloadItemStatus GetItemStatus(DeemixQueueItem item)
        {
            if (item.Failed)
            {
                return DownloadItemStatus.Failed;
            }

            if (item.Progress == 0)
            {
                return DownloadItemStatus.Queued;
            }

            if (item.Progress < 100)
            {
                return DownloadItemStatus.Downloading;
            }

            return DownloadItemStatus.Completed;
        }
    }
}
