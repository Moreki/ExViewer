﻿using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using Windows.Foundation;
using static System.Runtime.InteropServices.WindowsRuntime.AsyncInfo;
using HtmlAgilityPack;
using System.Linq;
using System.Text.RegularExpressions;
using System.IO;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading.Tasks;
using Windows.Graphics.Imaging;
using System.Threading;
using Windows.Storage;
using ExClient.Models;
using System.Collections;
using Windows.Web.Http;

namespace ExClient
{
    public class SaveGalleryProgress
    {
        public int ImageLoaded
        {
            get; internal set;
        }

        public int ImageCount
        {
            get; internal set;
        }
    }

    public struct Language
    {
        internal Language(string name, LanguageModifier modifier)
        {
            var ca = name.ToCharArray();
            ca[0] = char.ToUpperInvariant(ca[0]);
            Name = new string(ca);
            Modifier = modifier;
        }

        public string Name
        {
            get;
        }

        public LanguageModifier Modifier
        {
            get;
        }

        public override string ToString()
        {
            switch(Modifier)
            {
            case LanguageModifier.Translated:
                return $"{Name} TR";
            case LanguageModifier.Rewrite:
                return $"{Name} RW";
            default:
                return Name;
            }
        }
    }

    public enum LanguageModifier
    {
        None,
        Translated,
        Rewrite
    }

    [JsonConverter(typeof(GalleryInfoConverter))]
    public struct GalleryInfo : IEquatable<GalleryInfo>
    {
        public GalleryInfo(long id, string token)
        {
            Id = id;
            Token = token;
        }

        public long Id
        {
            get;
        }

        public string Token
        {
            get;
        }

        public bool Equals(GalleryInfo other)
        {
            return this.Id == other.Id && this.Token == other.Token;
        }

        public override bool Equals(object obj)
        {
            if(obj == null || typeof(GalleryInfo) != obj.GetType())
            {
                return false;
            }
            return Equals((GalleryInfo)obj);
        }

        public override int GetHashCode()
        {
            return Id.GetHashCode() ^ (Token ?? "").GetHashCode();
        }
    }

    internal class GalleryInfoConverter : JsonConverter
    {
        public override bool CanConvert(Type objectType)
        {
            return typeof(GalleryInfo) == objectType;
        }

        public override object ReadJson(JsonReader reader, Type objectType, object existingValue, JsonSerializer serializer)
        {
            if(reader.TokenType != JsonToken.StartObject)
                return null;
            long gid = 0;
            string token = null;
            reader.Read();
            do
            {
                switch(reader.Value.ToString())
                {
                case "gid":
                    gid = reader.ReadAsInt32().GetValueOrDefault();
                    break;
                case "token":
                    token = reader.ReadAsString();
                    break;
                default:
                    break;
                }
                reader.Read();
            } while(reader.TokenType != JsonToken.EndObject);

            return new GalleryInfo(gid, token);
        }

        public override void WriteJson(JsonWriter writer, object value, JsonSerializer serializer)
        {
            var v = (GalleryInfo)value;
            writer.WriteStartArray();
            writer.WriteValue(v.Id);
            writer.WriteValue(v.Token);
            writer.WriteEndArray();
        }
    }

    [JsonObject]
    [System.Diagnostics.DebuggerDisplay(@"\{Id = {Id} Count = {Count} RecordCount = {RecordCount}\}")]
    public class Gallery : IncrementalLoadingCollection<GalleryImage>
    {
        internal static readonly int PageSize = 20;

        public static IAsyncOperation<Gallery> TryLoadGalleryAsync(long galleryId)
        {
            return Task.Run(async () =>
            {
                using(var db = new GalleryDb())
                {
                    var cm = db.SavedSet.SingleOrDefault(c => c.GalleryId == galleryId);
                    var gm = db.GallerySet.SingleOrDefault(g => g.Id == galleryId);
                    if(gm == null)
                        return null;
                    else
                    {
                        var r = (cm == null) ?
                             new Gallery(gm, true) :
                             new SavedGallery(gm, cm);
                        await r.InitAsync();
                        return r;
                    }
                }
            }).AsAsyncOperation();
        }

        private class GalleryData : Internal.ApiRequest
        {
            public override string Method => "gdata";

            public int @namespace => 1;

            public ICollection<GalleryInfo> gidlist
            {
                get;
            }

            public GalleryData(ICollection<GalleryInfo> list)
            {
                gidlist = list;
            }
        }

        public static IAsyncOperation<IList<Gallery>> FetchGalleriesAsync(ICollection<GalleryInfo> galleryInfo)
        {
            if(galleryInfo == null)
                throw new ArgumentNullException(nameof(galleryInfo));
            if(galleryInfo.Count > 25)
                throw new ArgumentException("Number of GalleryInfo is bigger than 25.", nameof(galleryInfo));
            return Run(async token =>
            {
                var type = new
                {
                    gmetadata = (IEnumerable<Gallery>)null
                };
                var str = await Client.Current.PostApiAsync(new GalleryData(galleryInfo));
                var deser = JsonConvert.DeserializeAnonymousType(str, type);
                var toAdd = deser.gmetadata.Select(item =>
                {
                    item.Owner = Client.Current;
                    var ignore = item.InitAsync();
                    return item;
                });
                return (IList<Gallery>)toAdd.ToList();
            });
        }

        internal const string ThumbFileName = "thumb.jpg";

        private static readonly IReadOnlyDictionary<string, Category> categoriesForRestApi = new Dictionary<string, Category>(StringComparer.OrdinalIgnoreCase)
        {
            ["Doujinshi"] = Category.Doujinshi,
            ["Manga"] = Category.Manga,
            ["Artist CG Sets"] = Category.ArtistCG,
            ["Game CG Sets"] = Category.GameCG,
            ["Western"] = Category.Western,
            ["Image Sets"] = Category.ImageSet,
            ["Non-H"] = Category.NonH,
            ["Cosplay"] = Category.Cosplay,
            ["Asian Porn"] = Category.AsianPorn,
            ["Misc"] = Category.Misc
        };

        public virtual IAsyncActionWithProgress<SaveGalleryProgress> SaveGalleryAsync(ConnectionStrategy strategy)
        {
            return Run<SaveGalleryProgress>(async (token, progress) =>
            {
                var toReport = new SaveGalleryProgress
                {
                    ImageCount = this.RecordCount,
                    ImageLoaded = -1
                };
                progress.Report(toReport);
                while(this.HasMoreItems)
                {
                    await this.LoadMoreItemsAsync((uint)PageSize);
                }
                toReport.ImageLoaded = 0;
                progress.Report(toReport);

                var loadTasks = this.Select(image => Task.Run(async () =>
                {
                    await image.LoadImageAsync(false, strategy, true);
                    lock(toReport)
                    {
                        toReport.ImageLoaded++;
                        progress.Report(toReport);
                    }
                }));
                await Task.WhenAll(loadTasks);

                var thumb = (await this.Owner.HttpClient.GetBufferAsync(this.ThumbUri)).ToArray();
                using(var db = new GalleryDb())
                {
                    var gid = this.Id;
                    var myModel = db.SavedSet.SingleOrDefault(model => model.GalleryId == gid);
                    if(myModel == null)
                    {
                        db.SavedSet.Add(new SavedGalleryModel().Update(this, thumb));
                    }
                    else
                    {
                        db.SavedSet.Update(myModel.Update(this, thumb));
                    }
                    await db.SaveChangesAsync();
                }
            });
        }

        protected Gallery(long id, string token, int loadedPageCount)
            : base(loadedPageCount)
        {
            this.Id = id;
            this.Token = token;
            this.GalleryUri = new Uri(galleryBaseUri, $"{Id.ToString()}/{Token}/");
        }

        internal Gallery(GalleryModel model, bool setThumbUriSource)
            : this(model.Id, model.Token, 0)
        {
            this.Id = model.Id;
            this.Available = model.Available;
            this.ArchiverKey = model.ArchiverKey;
            this.Token = model.Token;
            this.Title = model.Title;
            this.TitleJpn = model.TitleJpn;
            this.Category = model.Category;
            this.Uploader = model.Uploader;
            this.Posted = model.Posted;
            this.FileSize = model.FileSize;
            this.Expunged = model.Expunged;
            this.Rating = model.Rating;
            this.Tags = JsonConvert.DeserializeObject<IList<string>>(model.Tags).Select(t => Tag.Parse(t)).ToList().AsReadOnly();
            this.RecordCount = model.RecordCount;
            this.ThumbUri = new Uri(model.ThumbUri);
            this.setThumbUriWhenInit = setThumbUriSource;
            this.Owner = Client.Current;
            this.PageCount = MathHelper.GetPageCount(RecordCount, PageSize);
        }

        [JsonConstructor]
        internal Gallery(
            long gid,
            string error = null,
            string token = null,
            string archiver_key = null,
            string title = null,
            string title_jpn = null,
            string category = null,
            string thumb = null,
            string uploader = null,
            string posted = null,
            string filecount = null,
            long filesize = 0,
            bool expunged = true,
            string rating = null,
            string torrentcount = null,
            string[] tags = null)
            : this(gid, token, 0)
        {
            this.Id = gid;
            if(error != null)
            {
                Available = false;
                return;
            }
            Available = !expunged;
            try
            {
                this.Token = token;
                this.ArchiverKey = archiver_key;
                this.Title = HtmlEntity.DeEntitize(title);
                this.TitleJpn = HtmlEntity.DeEntitize(title_jpn);
                Category ca;
                if(!categoriesForRestApi.TryGetValue(category, out ca))
                    ca = Category.Unspecified;
                this.Category = ca;
                this.Uploader = HtmlEntity.DeEntitize(uploader);
                this.Posted = DateTimeOffset.FromUnixTimeSeconds(long.Parse(posted, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture));
                this.RecordCount = int.Parse(filecount, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture);
                this.FileSize = filesize;
                this.Expunged = expunged;
                this.Rating = double.Parse(rating, System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture);
                this.TorrentCount = int.Parse(torrentcount, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture);
                this.Tags = tags.Select(tag => Tag.Parse(tag)).ToList().AsReadOnly();
                this.ThumbUri = toExUri(thumb);
            }
            catch(Exception)
            {
                Available = false;
            }
            this.PageCount = MathHelper.GetPageCount(RecordCount, PageSize);
        }

        private static readonly Regex toExUriRegex = new Regex(@"(?<domain>((gt\d|ul)\.ehgt\.org)|(ehgt\.org/t)|((\d{1,3}\.){3}\d{1,3}))(?<body>.+)(?<tail>_l\.)", RegexOptions.Compiled | RegexOptions.ExplicitCapture);

        // from gtX.eght.org//_l.jpg
        // to   exhentai.org/t//_250.jpg
        private static Uri toExUri(string uri)
        {
            return new Uri(toExUriRegex.Replace(uri, @"exhentai.org/t${body}_250."));
        }

        private bool setThumbUriWhenInit = true;

        protected IAsyncAction InitAsync()
        {
            return Run(async token =>
            {
                await initCoreAsync();
                await InitOverrideAsync();
            });
        }

        private IAsyncAction initCoreAsync()
        {
            return Run(async token =>
            {
                if(!setThumbUriWhenInit)
                    return;
                var buffer = await Client.Current.HttpClient.GetBufferAsync(ThumbUri);
                using(var stream = buffer.AsRandomAccessStream())
                {
                    var decoder = await BitmapDecoder.CreateAsync(stream);
                    Thumb = await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);
                }
            });
        }

        protected virtual IAsyncAction InitOverrideAsync()
        {
            return Task.Run(() =>
            {
                using(var db = new GalleryDb())
                {
                    var gid = this.Id;
                    var myModel = db.GallerySet.SingleOrDefault(model => model.Id == gid);
                    if(myModel == null)
                    {
                        db.GallerySet.Add(new GalleryModel().Update(this));
                    }
                    else
                    {
                        db.GallerySet.Update(myModel.Update(this));
                    }
                    db.SaveChanges();
                }
            }).AsAsyncAction();
        }

        #region MetaData

        public long Id
        {
            get; protected set;
        }

        public bool Available
        {
            get; protected set;
        }

        public string Token
        {
            get; protected set;
        }

        public string ArchiverKey
        {
            get; protected set;
        }

        public string Title
        {
            get; protected set;
        }

        public string TitleJpn
        {
            get; protected set;
        }

        public Category Category
        {
            get; protected set;
        }

        private SoftwareBitmap thumbImage;

        public SoftwareBitmap Thumb
        {
            get
            {
                return thumbImage;
            }
            protected set
            {
                Set(ref thumbImage, value?.GetReadOnlyView());
            }
        }

        public Uri ThumbUri
        {
            get; protected set;
        }

        public string Uploader
        {
            get; protected set;
        }

        public DateTimeOffset Posted
        {
            get; protected set;
        }

        public long FileSize
        {
            get; protected set;
        }

        public bool Expunged
        {
            get; protected set;
        }

        public double Rating
        {
            get; protected set;
        }

        public int TorrentCount
        {
            get; protected set;
        }

        public IReadOnlyList<Tag> Tags
        {
            get; protected set;
        }

        private static readonly string[] technicalTags = new string[]
        {
            "rewrite",
            "translated"
        };

        private static readonly string[] naTags = new string[]
        {
            "speechless",
            "text cleaned"
        };

        public Language Language
        {
            get
            {
                if(Tags == null)
                    return default(Language);
                var modi = LanguageModifier.None;
                var language = (string)null;
                foreach(var item in this.Tags.Where(t => t.Namespace == Namespace.Language))
                {
                    if(modi == LanguageModifier.None)
                    {
                        switch(item.Content)
                        {
                        case "rewrite":
                            modi = LanguageModifier.Rewrite;
                            continue;
                        case "translated":
                            modi = LanguageModifier.Translated;
                            continue;
                        }
                    }
                    if(language == null)
                    {
                        if(naTags.Contains(item.Content))
                            language = "N/A";
                        else
                            language = item.Content;
                    }
                }
                return new Language(language ?? "japanese", modi);
            }
        }

        public FavoriteCategory FavoriteCategory
        {
            get
            {
                return favorite;
            }
            protected internal set
            {
                Set(ref favorite, value);
            }
        }

        private FavoriteCategory favorite;

        public string FavoriteNote
        {
            get
            {
                return favNote;
            }
            protected internal set
            {
                Set(ref favNote, value);
            }
        }

        private string favNote;

        #endregion

        protected internal Client Owner
        {
            get; protected set;
        }

        public Uri GalleryUri
        {
            get; private set;
        }

        private StorageFolder galleryFolder;

        public StorageFolder GalleryFolder
        {
            get
            {
                return galleryFolder;
            }
            private set
            {
                Set(ref galleryFolder, value);
            }
        }

        public IAsyncOperation<StorageFolder> GetFolderAsync()
        {
            return Run(async token =>
            {
                if(galleryFolder == null)
                    GalleryFolder = await StorageHelper.LocalCache.CreateFolderAsync(Id.ToString(), CreationCollisionOption.OpenIfExists);
                return galleryFolder;
            });
        }

        private static readonly Uri galleryBaseUri = new Uri(Client.RootUri, "g/");
        private static readonly Regex imgLinkMatcher = new Regex(@"/s/([0-9a-f]+)/(\d+)-(\d+)", RegexOptions.Compiled);
        internal static readonly Regex favStyleMatcher = new Regex(@"background-position:\s*0\s*px\s+-(\d+)\s*px", RegexOptions.Compiled);

        private void updateFavoriteInfo(HtmlDocument html)
        {
            var favNode = html.GetElementbyId("fav");
            var favContentNode = favNode.Element("div");
            this.FavoriteCategory = Owner.Favorites.GetCategory(favContentNode);
        }

        protected override IAsyncOperation<IList<GalleryImage>> LoadPageAsync(int pageIndex)
        {
            return Task.Run(async () =>
            {
                await this.GetFolderAsync();
                var needLoadComments = comments == null;
                var uri = new Uri(this.GalleryUri, $"?inline_set=ts_l&p={pageIndex.ToString()}{(needLoadComments ? "hc=1" : "")}");
                var request = this.Owner.PostStrAsync(uri, null);
                var res = await request;
                Internal.ApiRequest.UpdateToken(res);
                var html = new HtmlDocument();
                html.LoadHtml(res);
                updateFavoriteInfo(html);
                if(needLoadComments)
                    this.Comments = Comment.LoadComment(html);
                var pcNodes = html.DocumentNode.Descendants("td")
                    .Where(node => "document.location=this.firstChild.href" == node.GetAttributeValue("onclick", ""))
                    .Select(node =>
                    {
                        int number;
                        var succeed = int.TryParse(node.InnerText, out number);
                        return new
                        {
                            succeed,
                            number
                        };
                    })
                    .Where(select => select.succeed)
                    .DefaultIfEmpty(new
                    {
                        succeed = true,
                        number = 1
                    })
                    .Max(select => select.number);
                PageCount = pcNodes;
                var pics = from node in html.GetElementbyId("gdt").Descendants("div")
                           where node.GetAttributeValue("class", null) == "gdtl"
                           let nodeA = node.Descendants("a").Single()
                           let nodeI = nodeA.Descendants("img").Single()
                           let thumb = nodeI.GetAttributeValue("src", null)
                           let imgLink = nodeA.GetAttributeValue("href", null)
                           let match = imgLinkMatcher.Match(nodeA.GetAttributeValue("href", ""))
                           where match.Success && thumb != null
                           select new
                           {
                               pageId = int.Parse(match.Groups[3].Value, System.Globalization.NumberStyles.Integer),
                               imageKey = match.Groups[1].Value,
                               thumbUri = new Uri(thumb)
                           };
                var toAdd = new List<GalleryImage>(PageSize);
                using(var db = new GalleryDb())
                {
                    foreach(var page in pics)
                    {
                        var imageKey = page.imageKey;
                        var imageModel = db.ImageSet.FirstOrDefault(im => im.ImageKey == imageKey);
                        if(imageModel != null)
                        {
                            // Load cache
                            var galleryImage = await GalleryImage.LoadCachedImageAsync(this, imageModel);
                            if(galleryImage != null)
                            {
                                toAdd.Add(galleryImage);
                                continue;
                            }
                        }
                        toAdd.Add(new GalleryImage(this, page.pageId, page.imageKey, page.thumbUri));
                    }
                }
                return (IList<GalleryImage>)toAdd;
            }).AsAsyncOperation();
        }

        public IAsyncOperation<ReadOnlyCollection<TorrentInfo>> LoadTorrnetsAsync()
        {
            return TorrentInfo.LoadTorrentsAsync(this);
        }

        private ReadOnlyCollection<Comment> comments;

        public ReadOnlyCollection<Comment> Comments
        {
            get
            {
                return comments;
            }
            protected set
            {
                Set(ref comments, value);
            }
        }

        public IAsyncOperation<ReadOnlyCollection<Comment>> LoadCommentsAsync()
        {
            return Run(async token =>
            {
                Comments = await Comment.LoadCommentsAsync(this);
                return comments;
            });
        }

        private IEnumerable<KeyValuePair<string, string>> getInfo(string favcat, string favnote)
        {
            yield return new KeyValuePair<string, string>("apply", "Apply+Changes");
            yield return new KeyValuePair<string, string>("favcat", favcat);
            yield return new KeyValuePair<string, string>("favnote", favnote);
            yield return new KeyValuePair<string, string>("update", "1");
        }

        private static readonly Regex favNoteMatcher = new Regex(@"'Note: (.+?) ';", RegexOptions.Compiled);

        public IAsyncAction AddToFavorites(FavoriteCategory category, string note)
        {
            return Run(async token =>
            {
                var requestUri = new Uri(Client.RootUri, $"gallerypopups.php?gid={Id}&t={Token}&act=addfav");
                var requestContent = new HttpFormUrlEncodedContent(getInfo(category == null ? "favdel" : category.CollectionIndex.ToString(), note));
                var response = await Owner.HttpClient.PostAsync(requestUri, requestContent);
                var responseContent = await response.Content.ReadAsStringAsync();
                var match = favNoteMatcher.Match(responseContent, 1300);
                if(match.Success)
                    FavoriteNote = HtmlEntity.DeEntitize(match.Groups[1].Value);
                else
                    FavoriteNote = null;
                FavoriteCategory = category;
            });
        }

        public virtual IAsyncAction DeleteAsync()
        {
            return Task.Run(async () =>
            {
                var gid = this.Id;
                await GetFolderAsync();
                var temp = GalleryFolder;
                GalleryFolder = null;
                await temp.DeleteAsync();
                using(var db = new GalleryDb())
                {
                    db.ImageSet.RemoveRange(db.ImageSet.Where(i => i.OwnerId == gid));
                    await db.SaveChangesAsync();
                }
                var c = this.RecordCount;
                ResetAll();
                this.RecordCount = c;
                this.PageCount = MathHelper.GetPageCount(RecordCount, PageSize);
            }).AsAsyncAction();
        }
    }
}