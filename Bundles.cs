//Note that this crashes the editor when using ios scene bundles (works with mac bundles)
//Better add EDITOR_INCLUDED_BUNDLES to Scripting Define Symbols in PlayerSettings
//#define EDITOR_INCLUDED_BUNDLES

using UnityEngine;
using System.Linq;
using System.IO;
using System.Collections;
using System.Collections.Generic;
using System;
using System.Text;
using TFG;
using TFG.Modules;


#if (!UNITY_EDITOR || EDITOR_INCLUDED_BUNDLES) && !RETAIL_BUILD
using UnityEngine.Assertions;
#endif

public class Bundles : MonoBehaviour
{
    #if !RETAIL_BUILD
    static readonly string BaseUrl = "www.baidu.com";
	// static readonly string BaseUrl = "www.baidu.com";
#elif UNITY_CHINA
    static readonly string BaseUrl = "www.baidu.com";
#else
    static readonly string BaseUrl = "www.baidu.com";
    //static readonly string BaseUrl = "www.baidu.com";
#endif

	[Serializable]
    public class Manifest
    {
        [Serializable]
        public class Bundle
        {
            public string Name;
            public uint Crc;
            public int Version;
            public long Size;
            public bool Included;
            public bool Preload;
            public bool IsScene;
            public List<string> Assets;
            public List<string> Dependencies;
        }

        public List<Bundle> Bundles = new List<Bundle>();

        Dictionary<string, Bundle> _bundlesByName = new Dictionary<string, Bundle>();

        public void Setup()
        {
            _bundlesByName = new Dictionary<string, Bundle>();
            foreach (var bundle in Bundles)
            {
                _bundlesByName[bundle.Name] = bundle;
            }
        }

        public bool IsLoaded
        {
            get{return Bundles.Count > 0; }
        }

        public void Add(Bundle bundle)
        {
            Bundles.Add(bundle);
            _bundlesByName[bundle.Name] = bundle;
        }

        public Bundle Get(string name)
        {
            return _bundlesByName.ContainsKey(name) ? _bundlesByName[name] : null;
        }

        public Bundle FindBundleAsset(string assetName)
        {
            return Bundles.Where(b => !b.IsScene).FirstOrDefault(b => b.Assets.Contains(assetName));
        }

        public Bundle FindBundleScene(string sceneName)
        {
            return Bundles.Where(b => b.IsScene).FirstOrDefault(b => b.Assets.Contains(sceneName));
        }

        public bool BundleExists(string bundleName)
        {
            return Bundles.Exists(b => b.Name == bundleName);
        }

        // Depth-first post-order, root not included
        IEnumerable<Bundle> CollectDependenciesImpl(Bundle bundle, HashSet<Bundle> visited)
        {
            foreach (var bundleName in bundle.Dependencies)
            {
                var dependency = Get(bundleName);

                if (visited.Add(dependency))
                {
                    foreach (var sub in CollectDependenciesImpl(dependency, visited))
                        yield return sub;

                    yield return dependency;
                }
            }
        }

        public IEnumerable<Bundle> CollectDependencies(Bundle root)
        {
            return CollectDependenciesImpl(root, new HashSet<Bundle>());
        }

        public IEnumerable<Bundle> CollectDependencies(IEnumerable<Bundle> roots)
        {
            var visited = new HashSet<Bundle>();
            foreach (var root in roots)
            {
                foreach (var dependency in CollectDependenciesImpl(root, visited))
                    yield return dependency;
            }
        }
    }

    public static readonly string AssetNamePrefix = "";
    const float RetryInterval = 2;

    public class BundleState
    {
        public enum Status
        {
            Unknown = 0,
            Downloading,
            Cached,
            Loading,
            Ready
        }

        public enum Error
        {
            None,
            NoInternetConnection,
            DownloadFailed,
            InvalidBundle
        }

        public BundleState(Manifest.Bundle bundle)
        {
            Bundle = bundle;
        }
        Error _lastError = Error.None;
        float _lastTryTime;

        public Manifest.Bundle Bundle;
        public WWW Www = null;
        public bool Requested = false;
        public AssetBundle AssetBundle = null;
        public float LastUseTime = 0.0f;

        public Error LastError {
            get{ return _lastError; }
            set{
                _lastError = value;
                _lastTryTime = Time.realtimeSinceStartup;
            }
        }

        public bool Waiting{
            get{ return Time.realtimeSinceStartup < _lastTryTime + RetryInterval; }
        }

        public Status CurrentStatus
        {
            get
            {
                if (IsReady)
                    return Status.Ready;

                if (IsLoading)
                    return Status.Loading;

                if (IsCached)
                    return Status.Cached;

                if (IsDownloading)
                    return Status.Downloading;

                return Status.Unknown;
            }
        }

        public bool IsReady
        {
            get { return AssetBundle != null; }
        }

        public bool IsLoading
        {
            get { return IsCached && Www != null; }
        }

        public bool IsCached
        {
            get { return IsReady || Bundle.Included || (Caching.ready && Caching.IsVersionCached(Bundle.Name, Bundle.Version)); }
        }

        public bool IsDownloading
        {
            get { return !IsCached && Www != null; }
        }

        public long AvailableSize
        {
            get
            {
                if (IsCached)
                    return Bundle.Size;

                if (Www != null)
                    return (long)(Www.progress * (float)Bundle.Size);

                return 0;
            }
        }

        public void Touch()
        {
            LastUseTime = Time.realtimeSinceStartup;
        }

        public void SetReady(AssetBundle assetBundle)
        {
            AssetBundle = assetBundle;
            LastError = Error.None;
            Touch();
        }

        public void Unload()
        {
            if (AssetBundle == null)
                return;

            AssetBundle.Unload(false);
            AssetBundle = null;
            Requested = false;
        }
    }

    #if UNITY_ANDROID
    static readonly int MaxConnections = 3;
    #else
    static readonly int MaxConnections = 6;
    #endif
    static readonly int MaxLoadedBundles = 30;
    static readonly string ManifestName = "Manifest";


    Manifest _manifest;
    Dictionary<Manifest.Bundle, BundleState> _bundlesStates = new Dictionary<Manifest.Bundle, BundleState>();
    List<BundleState> _downloadQueue = new List<BundleState>();
    List<BundleState> _loadingList = new List<BundleState>();
    List<BundleState> _loadedList = new List<BundleState>();
    bool _allWeaponsCached = false;
    bool _allRegionsCached = false;
    float _idleTime = 5.0f;

    void BundleLoaded(BundleState bundle)
    {
        if(bundle.Bundle.Included)
            return;

        if(_loadedList.Contains (bundle))
        {
            _loadedList.Remove(bundle);
            _loadedList.Add(bundle);
            return;
        }

        _loadedList.Add(bundle);
        if(_loadedList.Count > MaxLoadedBundles)
        {
            var first = _loadedList[0];
            _loadedList.RemoveAt(0);

            first.Unload();
        }
    }

    public BundleState[] DownloadQueue
    {
        get
        {
            return _downloadQueue.ToArray();
        }
    }

    public BundleState[] LoadedBundles
    {
        get
        {
            return _loadedList.ToArray();
        }
    }

    string PlatformUrl
    {
        get
        {
#if UNITY_CHINA && RETAIL_BUILD
            return BaseUrl;
#else
            return BaseUrl;
#endif
        }
    }

    string BuildPlatformUrl()
    {
        switch (Application.platform)
        {
            case RuntimePlatform.IPhonePlayer:
                return BaseUrl + "ios/";

            case RuntimePlatform.Android:
                return BaseUrl + "Android/";

            case RuntimePlatform.WindowsPlayer:
                return BaseUrl + "windows/";

#if UNITY_EDITOR
            case RuntimePlatform.OSXEditor:
            case RuntimePlatform.WindowsEditor:
            {
                switch (UnityEditor.EditorUserBuildSettings.activeBuildTarget)
                {
                    case UnityEditor.BuildTarget.iOS:
                        return BaseUrl + "ios/";
                    case UnityEditor.BuildTarget.Android:
                        return BaseUrl + "Android/";
                    case UnityEditor.BuildTarget.WebGL:
                        return BaseUrl + "webgl/";
                    case UnityEditor.BuildTarget.StandaloneWindows:
                    case UnityEditor.BuildTarget.StandaloneWindows64:
                        return BaseUrl + "windows/";
                }

                break;
            }
#endif
        }

        throw new InvalidOperationException("Unsupported bundle platform");
    }

    #if UNITY_EDITOR && EDITOR_INCLUDED_BUNDLES
    string EditorBundlesPath
    {
        get
        {
            var path = Path.GetDirectoryName(Application.dataPath);
            path = Path.Combine(path, "Build");
            path = Path.Combine(path, "Bundles");
            return path;
        }
    }

    string IncludedBundlesPath { get {
//            Debug.Log((Path.Combine(EditorBundlesPath, "Include") + "/"));
            return Path.Combine(EditorBundlesPath, "Include") + "/"; } }
    string BundlesUrl { get { return "file://" + Path.Combine(EditorBundlesPath, "Download") + "/"; } }
    //string BundlesUrl { get { return PlatformUrl + "1213" + "/"; } }
    #else
    string IncludedBundlesPath { get { return Path.Combine(Application.streamingAssetsPath, "Bundles") + "/"; } }
    string BundlesUrl
    {
        get { return string.Format(PlatformUrl, App.BuildNumber); }
    }
    #endif

    string IncludedBundlesUrl
    {
        get
        {
            if (IncludedBundlesPath.Contains("://"))
                return IncludedBundlesPath;

            return "file://" + IncludedBundlesPath;
        }
    }

    void Awake()
    {
        DontDestroyOnLoad(gameObject);
        #if !UNITY_EDITOR || EDITOR_INCLUDED_BUNDLES
        StartCoroutine(LoadManifest());
        #endif
    }

    void CleanupBundles()
    {
        var loadedBundles = _bundlesStates.Where(p => p.Value.IsReady);
        while (loadedBundles.Count() > MaxLoadedBundles)
        {
            Manifest.Bundle olderBundle = null;
            float olderTime = float.MaxValue;

            foreach (var pair in loadedBundles)
            {
                if (pair.Value.LastUseTime < olderTime)
                {
                    olderBundle = pair.Key;
                    olderTime = pair.Value.LastUseTime;
                }
            }

            if (olderBundle != null)
            {
                _bundlesStates[olderBundle].Unload();
            }
        }
    }

    static Manifest CreateManifest(string content)
    {
        if (string.IsNullOrEmpty(content)) return null;

        var result = JsonUtility.FromJson<Manifest>(content);
        result.Setup();
        return result;
    }

    IEnumerator LoadManifest()
    {
        App.ExtraLoading = true;

        Manifest manifest = null;
        string manifestContent = null;

        #if UNITY_IPHONE
        var manifestPath = Path.Combine(IncludedBundlesPath, ManifestName);
        try
        {
            manifestContent = File.ReadAllText(manifestPath);
        }
        catch
        {
            Debug.LogError("Error reading manifest content");
        }
        #else
        using (var www = new WWW(IncludedBundlesUrl + ManifestName))
        {
            yield return www;
            manifestContent = www.text;
        }
        #endif

        manifest = CreateManifest(manifestContent);

        if (manifest != null)
            yield return StartCoroutine(SetManifest(manifest));

        App.ExtraLoading = false;
    }

    IEnumerator LoadIncludedAssetBundle(Manifest.Bundle bundle)
    {
        #if UNITY_IPHONE
        yield return AssetBundle.LoadFromFile(Path.Combine(IncludedBundlesPath, bundle.Name));
        #else
        using (var www = WWW.LoadFromCacheOrDownload(IncludedBundlesUrl + bundle.Name, bundle.Version, 0))
        {
            yield return www;
            yield return www.assetBundle;
        }
        #endif
    }

    string CachePath
    {
        get
        {
#if UNITY_EDITOR
            if (Application.platform == RuntimePlatform.OSXEditor)
                return Environment.GetFolderPath(Environment.SpecialFolder.Personal) + "/library/Caches/Unity/Fun Games;
            return string.Empty;
#elif UNITY_IPHONE
            return Directory.GetParent(Application.temporaryCachePath)+"/UnityCache/Shared";
#elif UNITY_ANDROID
            return Directory.GetParent(Application.temporaryCachePath)+"/files/UnityCache/Shared";
#else
            return string.Empty;
#endif
        }
    }

    void DeleteOldBundles(Manifest manifest)
    {
        if(!Directory.Exists(CachePath))
            return;

#if !UNITY_WEBPLAYER

        string[] bundles = null;

        try
        {
            bundles = Directory.GetDirectories(CachePath);
        }
        catch (Exception ex)
        {
            Debug.LogError("GetDirectories exception: " + ex.Message);
            bundles = null;
        }
        if (bundles == null) return;

        var correctBundles = new List<string>();

        foreach (var bundle in manifest.Bundles)
        {
            correctBundles.Add(BundleCode(bundle));
        }

        foreach(var bundlePath in bundles)
        {
            var bundleName = bundlePath.Split('/').Last();

            if (!correctBundles.Contains(bundleName))
            {
                try
                {
                    Directory.Delete(bundlePath,true);
                }
                catch (Exception ex)
                {
                    Debug.LogError("Delete exception: " + ex.Message);
                }
            }
        }
#endif
    }

    public void DeleteUnusedBundles()
    {
        foreach (var region in App.User.Regions)
            if (!region.ShouldKeepCached)
                region.Uncache();

        var timeLimited = App.TimeLimitedEventInteractor;
        var currentEvent = timeLimited.CurrentEvent;
        foreach (var ev in timeLimited.AllEvents())
            if (ev != currentEvent)
                UncacheEvent(ev);

        var tournament = App.TournamentManager;
        foreach (var week in tournament.AllWeeks)
            if (week != tournament.CurrentWeekData)
                UncacheTournament(week);

        if (ClearWeaponsCache)
        {
            var weapons = App.User.Inventory.OfType<WeaponData>()
                .OrderByDescending(w => w.LastTimeUsed).Skip(3)
                .Where(x => !x.WasRecentlyViewed)
                .Except(GetImportantWeapons());

            foreach (var weapon in weapons)
            {
                UncacheWeapon(weapon);
            }
        }

    }

    string BundleCode(Manifest.Bundle bundle)
    {
        var code = bundle.Name+(new Hash128 (0u, 0u, 0u, (uint)bundle.Version) );

        return SniperUtils.CalculateSha1Hash(code);
    }

    IEnumerator SetManifest(Manifest manifest)
    {
        if (_manifest != null)
            throw new System.Exception("Changing manifest isn't supported");

        DeleteOldBundles(manifest);

        foreach (var bundle in manifest.Bundles)
        {
            var bundleState = new BundleState(bundle);

            if (bundle.Preload && bundle.Included)
            {
                var assetBundle = this.StartCoroutine<AssetBundle>(LoadIncludedAssetBundle(bundle));
                yield return (Coroutine)assetBundle;

                if (assetBundle.HasValue)
                {
                    bundleState.SetReady(assetBundle.Value);
                    if (BundleReady != null)
                        BundleReady(bundleState.Bundle.Name);
                }
            }

            _bundlesStates.Add(bundle, bundleState);
        }

        _manifest = manifest;
        _idleTime = 5.0f;

        DeleteUnusedBundles();
    }

    void Update()
    {
        if (!App.IsValid) return;

        if (!Caching.ready || _manifest == null || !_manifest.IsLoaded)
            return;

        if(_loadingList.Count > 0)
        {
            _idleTime = 5.0f;
        }
        else if (_downloadQueue.Count > 0)
        {
            if(_downloadQueue.Any(d => d.Requested))
                _idleTime = 5.0f;

            var readyForDownload = _downloadQueue.Where(b => (b.Www == null && b.Waiting == false)).ToList();

            var numAvailable = MaxConnections - _downloadQueue.Count(b => b.IsDownloading);

            if (numAvailable > 0)
            {
                foreach (var entry in readyForDownload.Take(numAvailable))
                    StartCoroutine(DoDownload(entry));
            }
        }
        else if (_idleTime < 0.0f)
        {
            // If downloads are idle, in menus and connected to WiFi
            if (App.State == AppState.Menus && Application.internetReachability == NetworkReachability.ReachableViaLocalAreaNetwork)
            {
                // Download weapons and regions in background
                if (!_allRegionsCached)
                {
                    var nextRegion = App.User.Regions.Where(r => !r.IsEmpty && (r.ShouldKeepCached || r.IsRequested) ).FirstOrDefault(r => !IsRegionCached(r));
                    if (nextRegion != null)
                    {
                        _allRegionsCached = false;
                        CacheRegion(nextRegion);
                        Log("CacheRegion "+nextRegion.name);
                    }
                    else
                    {
                        _allRegionsCached = true;
                        Debug.Log("_allRegionsCached ");
                    }
                }
                else if (!ClearWeaponsCache)
                {
                    if (!_allWeaponsCached)
                    {
                        var nextWeapon = App.User.Inventory.OfType<WeaponData>().FirstOrDefault(w => !IsWeaponCached(w));
                        if (nextWeapon != null)
                        {
                            _allWeaponsCached = false;
                            Log("CacheWeapon "+nextWeapon.name);
                            CacheWeapon(nextWeapon);
                        }
                        else
                        {
                            _allWeaponsCached = true;
                            Log("_allWeaponsCached");
                        }
                    }
                }else if (ClearWeaponsCache)
                {
                    if (!_allWeaponsCached)
                    {
                        var importantWeapons = GetImportantWeapons();
                        var importantWeapon = importantWeapons.Where(w => w != null).FirstOrDefault(w => !IsWeaponCached(w));
                        if (importantWeapon != null)
                        {
                            _allWeaponsCached = false;
                            CacheWeapon(importantWeapon);
                        }
                        else
                        {
                            _allWeaponsCached = true;
                        }
                    }
                }
            }
        }
        else
        {
            _idleTime -= Time.unscaledDeltaTime;
        }
    }

    public bool ManifestReady
    {
        get
        {
            #if UNITY_EDITOR && !EDITOR_INCLUDED_BUNDLES
            return true;
            #else
            return HasBundles;
            #endif
        }
    }

    bool HasBundles
    {
        get { return _manifest != null; }
    }

    public bool HasBundle(Manifest.Bundle bundle)
    {
        return _bundlesStates.ContainsKey(bundle);
    }

    BundleState this[Manifest.Bundle bundle]
    {
        get { return _bundlesStates[bundle]; }
    }

    #if UNITY_ANDROID

    class DebugWWW{
        public string bundleName;
        public float progress;
        public float timeoutTime;
        public string error;
    }

    List<DebugWWW> _debugWWWs = new List<DebugWWW>();

    IEnumerator DoDownload(BundleState bundleState)
    {
        if(!App.IsValid || bundleState.CurrentStatus == BundleState.Status.Ready || bundleState.CurrentStatus == BundleState.Status.Cached)
        {
            _downloadQueue.Remove(bundleState);
            yield break;
        }

        try
        {
            var debugWWW = new DebugWWW();
            debugWWW.bundleName = bundleState.Bundle.Name;
            _debugWWWs.Add(debugWWW);

            if(_debugWWWs.Count > 8)
                _debugWWWs.RemoveAt(_debugWWWs.Count-1);

            if(Application.internetReachability ==  NetworkReachability.NotReachable)
            {
                bundleState.LastError = BundleState.Error.NoInternetConnection;
                debugWWW.error = "NoInternet";
                LogDownload("NoInternet", bundleState.Bundle.Name);
                Analytics.BundleDownloadError(bundleState.Bundle.Name, "NoInternet");
                bundleState.Www = null;

                yield break;
            }

           var www = WWW.LoadFromCacheOrDownload(BundlesUrl + bundleState.Bundle.Name,
                                              bundleState.Bundle.Version
                                              );
            //HACK: dispose hangs on some android devices if www.isDone is false
            WWWDisposer.AddWWWToDispose(www,bundleState);

            bundleState.Www = www;
            bundleState.LastError = BundleState.Error.None;

            float lastProgress = www.progress;;
            float timer = Time.realtimeSinceStartup;
            const float timeoutTime = 20;
            LogDownload("Start Download",bundleState.Bundle.Name);


            while(!www.isDone)
            {
                debugWWW.progress = www.progress;

                if(www.progress < 1 && Mathf.Approximately( www.progress, lastProgress))
                {
                    if( Time.realtimeSinceStartup >= timeoutTime+timer)
                    {
                        LogDownload("Download Timeout ",bundleState.Bundle.Name);
                        debugWWW.error = "Timeout";
                        bundleState.LastError = BundleState.Error.DownloadFailed;
                        Analytics.BundleDownloadError(bundleState.Bundle.Name, "DownloadTimeout");

                        LogDownload("Download Delay ",bundleState.Bundle.Name);
                        if(!www.isDone )
                        {
                            //HACK: dispose hangs on some android devices
                            //www.Dispose();
                            bundleState.Www = null;
                            LogDownload("Download break ",bundleState.Bundle.Name);

                            yield break;
                        }

                    }
                }
                else
                {
                    timer = Time.realtimeSinceStartup;
                }
                debugWWW.timeoutTime = Time.realtimeSinceStartup - timer;
                lastProgress = www.progress;

                yield return null;
            }

            if (string.IsNullOrEmpty(www.error))
            {
                if (BundleCached != null)
                    BundleCached(bundleState.Bundle.Name);

                if (bundleState.Requested)
                {
                    var assetBundle = www.assetBundle;
                    if (assetBundle)
                    {

                        if(bundleState.Requested)
                        {
                            _idleTime = 5.0f;
                            LogDownload("Weapon Request Finished",bundleState.Bundle.Name);
                        }
                        else {
                            LogDownload("Cache Finished",bundleState.Bundle.Name);
                            _idleTime = 0.5f;
                        }

                        bundleState.SetReady(assetBundle);

                        _downloadQueue.Remove(bundleState);
                        BundleLoaded(bundleState);

                        if (BundleReady != null)
                            BundleReady(bundleState.Bundle.Name);

                        Analytics.BundleDownloaded(bundleState.Bundle.Name);
                    }
                    else
                    {
                        bundleState.Www = null;
                        _idleTime = 0.5f;
                        bundleState.LastError = BundleState.Error.InvalidBundle;
                        LogDownload("Bundle Invalid", bundleState.Bundle.Name);
                        Analytics.BundleDownloadError(bundleState.Bundle.Name, "InvalidBundle");
                    }
                }
                else
                {
                    LogDownload("Cache Finished",bundleState.Bundle.Name);
                    _downloadQueue.Remove(bundleState);
                    _idleTime = 0.5f;
                    Analytics.BundleDownloaded(bundleState.Bundle.Name);
                }
            }
            else
            {
                if (Application.internetReachability == NetworkReachability.NotReachable)
                {
                    bundleState.LastError = BundleState.Error.NoInternetConnection;
                    debugWWW.error = "NoInternet";
                    LogDownload("NoInternet", bundleState.Bundle.Name,www);
                    Analytics.BundleDownloadError(bundleState.Bundle.Name, "NoInternet");
                    bundleState.Www = null;
                    _idleTime = 0.5f;
                }
                else
                {
                    bundleState.LastError = BundleState.Error.DownloadFailed;

                    LogDownload("Download Failed", bundleState.Bundle.Name,www );
                    debugWWW.error = www.error;
                    Analytics.BundleDownloadError(bundleState.Bundle.Name, "DownloadFailed");
                    bundleState.Www = null;
                    _idleTime = 0.5f;
                }
            }

        }
        finally
        {
            bundleState.Www = null;
            if(!bundleState.Requested)
                _downloadQueue.Remove(bundleState);
        }
    }
    /*
#if !RETAIL_BUILD
    void OnGUI()
    {
        float verticalOffset = 20;
        for( int i =0 ; i < _debugWWWs.Count; i ++)
        {
            GUI.Label(new Rect(10,100+3*i*verticalOffset,2000,verticalOffset), _debugWWWs[i].bundleName);
            string content = "progress: "+ _debugWWWs[i].progress.ToString("F4")+" timeout: "+ _debugWWWs[i].timeoutTime.ToString("F1")+" "+_debugWWWs[i].error ;
            GUI.Label(new Rect(10,100+(3*i+1)*verticalOffset,2000,verticalOffset), content);
        }
    }
#endif
     */


#else
    IEnumerator DoDownload(BundleState bundleState)
    {
        if(!App.IsValid || bundleState.CurrentStatus == BundleState.Status.Ready || bundleState.CurrentStatus == BundleState.Status.Cached)
        {
            _downloadQueue.Remove(bundleState);
            yield break;
        }

        try
        {
            using (var www = WWW.LoadFromCacheOrDownload(BundlesUrl + bundleState.Bundle.Name,
                                                         bundleState.Bundle.Version,
                    bundleState.Bundle.Crc))
            {
                bundleState.Www = www;
                bundleState.LastError = BundleState.Error.None;

                LogDownload("Start Download",bundleState.Bundle.Name +" "+ bundleState.CurrentStatus);

                yield return www;

                if (string.IsNullOrEmpty(www.error))
                {
                    if (BundleCached != null)
                        BundleCached(bundleState.Bundle.Name);

                    if (bundleState.Requested)
                    {
                        var assetBundle = www.assetBundle;
                        if (assetBundle)
                        {

                            if(bundleState.Requested)
                            {
                                _idleTime = 5;
                                LogDownload("Weapon Request Finished",bundleState.Bundle.Name);
                            }
                            else LogDownload("Cache Finished",bundleState.Bundle.Name);


                            bundleState.SetReady(assetBundle);

                            _downloadQueue.Remove(bundleState);
                            BundleLoaded(bundleState);

                            if (BundleReady != null)
                                BundleReady(bundleState.Bundle.Name);

                            Analytics.BundleDownloaded(bundleState.Bundle.Name);
                        }
                        else
                        {
                            bundleState.Www = null;
                            bundleState.LastError = BundleState.Error.InvalidBundle;
                            LogDownload("Bundle Invalid", bundleState.Bundle.Name);
                            Analytics.BundleDownloadError(bundleState.Bundle.Name, "InvalidBundle");
                        }
                    }
                    else
                    {
                        LogDownload("Cache Finished",bundleState.Bundle.Name);
                        _downloadQueue.Remove(bundleState);
                       // _idleTime = 0.0f;
                        Analytics.BundleDownloaded(bundleState.Bundle.Name);
                    }
                }
                else
                {
                    if (Application.internetReachability == NetworkReachability.NotReachable)
                    {
                        bundleState.LastError = BundleState.Error.NoInternetConnection;

                        LogDownload("NoInternet", bundleState.Bundle.Name,www);
                        Analytics.BundleDownloadError(bundleState.Bundle.Name, "NoInternet");
                        bundleState.Www = null;
                    }
                    else
                    {
                        bundleState.LastError = BundleState.Error.DownloadFailed;

                        LogDownload("Download Failed", bundleState.Bundle.Name,www );

                        if(www.error.Contains("already loaded"))
                        {
                            var bundle = this[bundleState.Bundle];
                            if(bundle != null)
                                bundle.Unload();
                        }

                        Analytics.BundleDownloadError(bundleState.Bundle.Name, "DownloadFailed");
                        bundleState.Www = null;
                    }
                }
            }
        }
        finally
        {
            bundleState.Www = null;
            if(!bundleState.Requested)
                _downloadQueue.Remove(bundleState);
        }
    }
#endif

    public void CheckRegionCache()
    {
        _allRegionsCached = false;
    }

    static void LogDownload(string message, string bundleName,WWW www = null)
    {
        Log(message + " " + bundleName + (www != null ? " " + www.error : string.Empty));
    }

    static void Log(string message)
    {
#if !RETAIL_BUILD
        if (!Cheats.LogBundles) return;
        Debug.Log(message);
#endif
    }

    IEnumerator DoLoad(BundleState bundleState)
    {

        try
        {
            if (bundleState.Bundle.Included)
            {
                var assetBundle = this.StartCoroutine<AssetBundle>(LoadIncludedAssetBundle(bundleState.Bundle));
                yield return (Coroutine)assetBundle;

                if (assetBundle.HasValue)
                {
                    _idleTime = 5;
                    bundleState.SetReady(assetBundle.Value);
                    if (BundleReady != null)
                        BundleReady(bundleState.Bundle.Name);
                }
                else
                {
                    QueueDownload(bundleState);
                }
            }
            else
            {
                LogDownload("Load from cache",bundleState.Bundle.Name);

                _loadingList.Add(bundleState);
                using (var www = WWW.LoadFromCacheOrDownload(BundlesUrl + bundleState.Bundle.Name,
                                                             bundleState.Bundle.Version
                        ))
                {
                    _idleTime = 5.0f;
                    bundleState.Www = www;

                    yield return www;

                    if (string.IsNullOrEmpty(www.error))
                    {
                        var assetBundle = www.assetBundle;
                        if (assetBundle != null)
                        {
                            _idleTime = 5.0f;
                            LogDownload("Bundle loaded",bundleState.Bundle.Name);
                            bundleState.SetReady(assetBundle);

                            BundleLoaded(bundleState);

                            if (BundleReady != null)
                                BundleReady(bundleState.Bundle.Name);
                        }
                        else
                        {
                            bundleState.Www = null;
                            bundleState.LastError = BundleState.Error.InvalidBundle;
                            LogDownload("Load bundle invalid",bundleState.Bundle.Name);
                            QueueDownload(bundleState);
                        }
                    }
                    else
                    {
                        LogDownload("Load error",bundleState.Bundle.Name,www);
                        QueueDownload(bundleState);
                    }
                }
            }
        }
        finally
        {
            _loadingList.Remove(bundleState);
            bundleState.Www = null;
        }
    }

    bool Cache(Manifest.Bundle bundle)
    {
        var bundleState = this[bundle];
        if (bundleState.IsCached)
            return true;

        QueueDownload(bundleState);
        return false;
    }

    bool QueueDownload(BundleState bundleState)
    {
        if (_downloadQueue.Contains(bundleState))
            return false;
//        Debug.Log("Queue "+bundleState.Bundle.Name);
        _downloadQueue.Add(bundleState);
        return true;
    }

    bool Request(Manifest.Bundle bundle)
    {
        var bundleState = this[bundle];
        bundleState.Requested = true;

        if (bundleState.IsReady)
        {
           // LogDownload("Bundle Ready",bundleState.Bundle.Name);
            bundleState.Touch();
            BundleLoaded(bundleState);
            return true;
        }

        if (bundleState.IsCached)
        {
            LogDownload("Bundle Cached, loading",bundleState.Bundle.Name);
            if (!bundleState.IsLoading)
                StartCoroutine(DoLoad(bundleState));
        }
        else
        {
            LogDownload("Queueing Bundle",bundleState.Bundle.Name);
            QueueDownload(bundleState);
        }

        return false;
    }

    public event System.Action<string> BundleCached;
    public event System.Action<string> BundleReady;

    public bool IsConnectionAvailable
    {
        get
        {
            if (Application.internetReachability == NetworkReachability.NotReachable)
                return false;

            // Any downloads in progress
            if (_bundlesStates.Where(p => p.Value.Www != null).Any(p => string.IsNullOrEmpty(p.Value.Www.error)))
                return true;

            return true;
        }
    }

    public T LoadAsset<T>(string assetName) where T: UnityEngine.Object
    {
#if UNITY_EDITOR && !EDITOR_INCLUDED_BUNDLES
        return null;
#else
        if (!HasBundles)
            return null;

        var bundle = _manifest.FindBundleAsset(assetName);
        return LoadAsset<T>(assetName, bundle);
#endif
    }

    public T LoadAsset<T>(string assetName, Manifest.Bundle bundle) where T: UnityEngine.Object
    {
#if UNITY_EDITOR && !EDITOR_INCLUDED_BUNDLES
        return null;
#else
        if (bundle == null)
            return null;

        if (!Request(bundle))
            return null;

        return this[bundle].AssetBundle.LoadAsset<T>(AssetNamePrefix + assetName);
#endif
    }

    public struct BundlesDownloadState
    {
        public enum Status
        {
            Cached,
            Downloading,
            ErrorNoInternetConnection = -1,
            ErrorDownloadFailed = -2,
            ErrorNoBundles = -3
        }

        public BundlesDownloadState(Status status, long readySize, long totalSize)
        {
            CurrentStatus = status;
            AvailableSize = readySize;
            TotalSize = totalSize;
        }

        public Status CurrentStatus;

        public long AvailableSize;
        public long TotalSize;

        public float Progress
        {
            get { return (float)AvailableSize / (float)TotalSize; }
        }
    }

    public BundlesDownloadState GetDownloadState(IEnumerable<Manifest.Bundle> bundles)
    {
        #if UNITY_EDITOR && !EDITOR_INCLUDED_BUNDLES
        return new BundlesDownloadState(BundlesDownloadState.Status.Cached, 1, 1);
        #else
        if (!HasBundles)
            return new BundlesDownloadState(BundlesDownloadState.Status.ErrorNoBundles, 0, 1);

        long totalSize = 0;
        long availableSize = 0;
        bool hasError = false;
        bool noConnection = false;

        foreach (var bundle in bundles)
        {
            var bundleState = _bundlesStates[bundle];

            totalSize += bundle.Size;
            availableSize += bundleState.AvailableSize;

            if (bundleState.LastError != BundleState.Error.None)
            {
                hasError = true;
                if (bundleState.LastError == BundleState.Error.NoInternetConnection)
                    noConnection = true;
            }
        }

        if (totalSize != availableSize)
        {
            if (hasError)
            {
                if (noConnection)
                {
                    return new BundlesDownloadState(BundlesDownloadState.Status.ErrorNoInternetConnection,
                                                 availableSize, totalSize);
                }
                else
                {
                    return new BundlesDownloadState(BundlesDownloadState.Status.ErrorDownloadFailed,
                                                 availableSize, totalSize);
                }
            }
            else
            {
                return new BundlesDownloadState(BundlesDownloadState.Status.Downloading,
                                             availableSize, totalSize);

            }
        }

        return new BundlesDownloadState(BundlesDownloadState.Status.Cached, availableSize, totalSize);
        #endif
    }

    IEnumerable<Manifest.Bundle> GetBundlesByName(IEnumerable<string> names)
    {
        #if UNITY_EDITOR && !EDITOR_INCLUDED_BUNDLES
        return new List<Manifest.Bundle>();
        #else
        if (HasBundles)
        {
            foreach (var name in names)
            {
                var bundle = _manifest.Get(name);
                if (bundle != null)
                    yield return bundle;
            }
        }
        else
        {
            Debug.LogError("Trying to access not ready bundle.");
        }
        #endif
    }

    bool HasDownloadedBundle(string name)
    {
        var bundle = _manifest.Get(name);
        if (bundle == null)
            return false;
        string bundleCode = BundleCode(bundle);

        try
        {
            var bundles = Directory.GetDirectories(CachePath);

            foreach (var bundlePath in bundles)
            {
                var bundleName = bundlePath.Split('/').Last();

                if (bundleCode == bundleName)
                {
                    return true;
                }
            }
            return false;
        }
        catch (DirectoryNotFoundException e){
                Debug.LogError("Directory not found:" + e.StackTrace);
                return false;
        }
    }

    #region MostWanted
    IEnumerable<Manifest.Bundle> GetMostWantedBundles()
    {
        return GetBundlesByName(GetMostWantedBundlesNames());
    }

    IEnumerable<string> GetMostWantedBundlesNames()
    {
        yield return "MostWantedCharacters";
    }

    public bool CacheMostWanted(Wanted.WantedCardData card)
    {
        #if UNITY_EDITOR && !EDITOR_INCLUDED_BUNDLES
        return true;
        #else
        if (!HasBundles)
            return false;

        bool ret = true;

        foreach (var bundle in GetMostWantedBundles())
            ret &= Cache(bundle);

        return ret;
        #endif
    }

    public bool RequestMostWanted(Wanted.WantedCardData card)
    {
        #if UNITY_EDITOR && !EDITOR_INCLUDED_BUNDLES
        return true;
        #else
        if (!HasBundles)
            return false;

        bool ret = true;

        foreach (var bundle in GetMostWantedBundles())
            ret &= Request(bundle);

        return ret;
        #endif
    }

    public bool IsMostWantedCached(Wanted.WantedCardData card)
    {
        #if UNITY_EDITOR && !EDITOR_INCLUDED_BUNDLES
        return true;
        #else
        if (!HasBundles)
            return false;

        foreach (var bundle in GetMostWantedBundles())
        {
            if (!this[bundle].IsCached)
                return false;
        }
        return true;
        #endif
    }

    public bool IsMostWantedReady(Wanted.WantedCardData card)
    {
        #if UNITY_EDITOR && !EDITOR_INCLUDED_BUNDLES
        return true;
        #else
        if (!HasBundles)
            return false;

        foreach (var bundle in GetMostWantedBundles())
        {
            if (!this[bundle].IsReady)
                return false;
        }
        return true;
        #endif
    }

    public BundlesDownloadState GetMostWantedDownloadState(Wanted.WantedCardData card)
    {
        return GetDownloadState(GetMostWantedBundles());
    }
    #endregion

    #region Weapons

    class Config
    {
        public static readonly Config Default = new Config();
        public bool ClearCacheEnabled = false;
    }

    static Config GetConfig()
    {
        return RemoteConfig.Get<Config>("WeaponsCache", Config.Default);
    }

    public static bool ClearWeaponsCache
    {
        get{ return GetConfig().ClearCacheEnabled; }
    }

    Dictionary<WeaponData,List<Manifest.Bundle>> _weaponBundles = new Dictionary<WeaponData, List<Manifest.Bundle>>();

    public IEnumerable<Manifest.Bundle> GetWeaponBundles(WeaponData weapon)
    {
        if (!HasBundles) return new List<Manifest.Bundle>();

        if (!_weaponBundles.ContainsKey(weapon))
        {
            _weaponBundles[weapon] = GetBundlesByName(GetWeaponBundlesNames(weapon)).ToList();
        }

        return _weaponBundles[weapon];
    }

    IEnumerable<WeaponData> GetImportantWeapons()
    {
        yield return App.User.SelectedWeapon;
        yield return App.Offers.WeaponSale.GetBestDiscountWeapon();
        yield return App.Offers.OneTimeOffer.CurrentOffer;
        yield return App.Offers.WorldOpsOneTimeOffer.CurrentOffer;
        yield return App.Offers.PvpOneTimeOffer.CurrentOffer;
        yield return App.DailyMission.CurrentDailyWeapon;
    }

    IEnumerable<string> GetWeaponBundlesNames(WeaponData weapon)
    {
        yield return "Weapon" + weapon.Name;
        bool localAreaNetwork = Application.internetReachability ==
                    NetworkReachability.ReachableViaLocalAreaNetwork;
        bool loadHigh = localAreaNetwork || HasDownloadedBundle("WeaponVariant" + weapon.Name + ".high");

        yield return "WeaponVariant" + weapon.Name + (loadHigh? ".high" : ".low");

        var query = weapon.AllPartsPrefabsNames.Select(pn => _manifest.FindBundleAsset(pn)).Distinct();
        foreach (var bundle in query)
            if (bundle != null)
                yield return bundle.Name;
    }

    public bool CacheWeapon(WeaponData weapon)
    {
        #if UNITY_EDITOR && !EDITOR_INCLUDED_BUNDLES
        return true;
        #else
        if (!HasBundles)
            return false;

        bool ret = true;

        foreach (var bundle in GetWeaponBundles(weapon))
            ret &= Cache(bundle);

        return ret;
        #endif
    }

    public void UncacheWeapon(WeaponData weapon){
        if(weapon == null)
            return;
        #if UNITY_EDITOR && !EDITOR_INCLUDED_BUNDLES
        return;
        #elif !UNITY_WEBPLAYER

        if(!Directory.Exists(CachePath))
            return;

        if (!HasBundles)
            return ;

        var weaponBundle = _manifest.Get("Weapon" + weapon.Name);

        string bundleCode = BundleCode(weaponBundle);

        var bundles = Directory.GetDirectories(CachePath);

        foreach(var bundlePath in bundles)
        {
            var bundleName = bundlePath.Split('/').Last();

            if (bundleCode == bundleName)
            {
                this[weaponBundle].Unload();
                Directory.Delete(bundlePath,true);
            }
        }
        #endif
    }

    public bool RequestWeapon(WeaponData weapon)
    {
        #if UNITY_EDITOR && !EDITOR_INCLUDED_BUNDLES
        return true;
        #else
        if (!HasBundles)
            return false;

        bool ret = true;

        foreach (var bundle in GetWeaponBundles(weapon))
            ret &= Request(bundle);

        return ret;
        #endif
    }

    public bool IsWeaponCached(WeaponData weapon)
    {
        #if UNITY_EDITOR && !EDITOR_INCLUDED_BUNDLES
        return true;
        #else
        if (!HasBundles)
            return false;

            #if !RETAIL_BUILD
        if(!App.User.Inventory.Contains(weapon))
            return false;
            #endif

        foreach (var bundle in GetWeaponBundles(weapon))
        {
            if (!this[bundle].IsCached)
                return false;
        }
        return true;
        #endif
    }

    public bool IsWeaponReady(WeaponData weapon)
    {
        #if UNITY_EDITOR && !EDITOR_INCLUDED_BUNDLES
        return true;
        #else
        if (!HasBundles)
            return false;

        foreach (var bundle in GetWeaponBundles(weapon))
        {
            if (!this[bundle].IsReady)
                return false;
        }
        return true;
        #endif
    }

    public BundlesDownloadState GetWeaponDownloadState(WeaponData weapon)
    {
        return GetDownloadState(GetWeaponBundles(weapon));
    }
    #endregion

    #region Regions  // So meta
    Dictionary<RegionData,List<Manifest.Bundle>> _regionBundles = new Dictionary<RegionData, List<Manifest.Bundle>>();
    IEnumerable<Manifest.Bundle> GetRegionBundles(RegionData region, LevelData level)
    {
        if (!HasBundles) return new List<Manifest.Bundle>();

        if (level == null)
        {
            if (!_regionBundles.ContainsKey(region))
                _regionBundles[region] = GetBundlesByName(GetRegionBundlesNames(region, null)).ToList();
            return _regionBundles[region];
        }

        return GetBundlesByName(GetRegionBundlesNames(region, level));
    }

    Dictionary<RegionData, List<Manifest.Bundle>> _regionPicturesBundles = new Dictionary<RegionData, List<Manifest.Bundle>> ();
    IEnumerable<Manifest.Bundle> GetRegionPicturesBundles (RegionData region)
    {
        if (!HasBundles) return new List<Manifest.Bundle> ();

        if (!_regionPicturesBundles.ContainsKey (region))
            _regionPicturesBundles [region] = GetBundlesByName (GetRegionPicturesBundlesNames (region)).ToList ();
        return _regionPicturesBundles [region];

    }

    Dictionary<string, List<Manifest.Bundle>> _timeLimitedBundles;
    public IEnumerable<Manifest.Bundle> GetTimeLimitedBundles(SceneryData scenery, string eventDataName)
    {
        if (_timeLimitedBundles == null)
            _timeLimitedBundles = new Dictionary<string, List<Manifest.Bundle>>();
        if (!_timeLimitedBundles.ContainsKey(eventDataName))
            _timeLimitedBundles[eventDataName] = GetBundlesByName(GetTimeLimitedBundleNames(scenery, eventDataName)).ToList();
        return _timeLimitedBundles[eventDataName];
    }

    Dictionary<SceneryData, List<Manifest.Bundle>> _tournamentBundles;
    IEnumerable<Manifest.Bundle> GetTournamentBundles(SceneryData scenery)
    {
        if (_tournamentBundles == null)
            _tournamentBundles = new Dictionary<SceneryData, List<Manifest.Bundle>>();
        if (!_tournamentBundles.ContainsKey(scenery))
            _tournamentBundles[scenery] = GetBundlesByName(GetTournamentBundleNames(scenery)).ToList();

        return _tournamentBundles[scenery];
    }

    List<Manifest.Bundle> _sharedSceneryScenesBundles;
    public IEnumerable<Manifest.Bundle> GetScenerySharedScenesBundles()
    {
        if (_sharedSceneryScenesBundles == null)
            _sharedSceneryScenesBundles = GetBundlesByName(GetScenerySharedScenesBundleNames()).ToList();
        return _sharedSceneryScenesBundles;
    }

    Dictionary<LevelData, IEnumerable<Manifest.Bundle>> _secondarySceneryScenesBundles;
    public IEnumerable<Manifest.Bundle> GetScenerySecondaryScenesBundles(LevelData level, SceneryData scenery)
    {
        if (_secondarySceneryScenesBundles == null)
        _secondarySceneryScenesBundles = new Dictionary<LevelData, IEnumerable<Manifest.Bundle>>();

        if (!_secondarySceneryScenesBundles.ContainsKey(level))
            _secondarySceneryScenesBundles[level] = GetBundlesByName(GetSecondaryLevelBundleNames(level, scenery)).ToList();
        return _secondarySceneryScenesBundles[level];
    }

    List<Manifest.Bundle> _pvpBundles;
    public IEnumerable<Manifest.Bundle> GetPvpBundles(SceneryData scenery)
    {
        if (_pvpBundles == null)
            _pvpBundles = GetBundlesByName(GetPvpBundleNames(scenery)).ToList();
        return _pvpBundles;
    }

    IEnumerable<string> GetSceneryBundlesNames(string name)
    {
        if (string.IsNullOrEmpty(name)) yield break;

        yield return "Scenery" + name;
        yield return "SceneryScenes" + name;
        yield return "SceneryCommonScenes" + name;
    }

    IEnumerable<string> GetTournamentContentBundlesNames(SceneryData scenery)
    {
        yield return string.Format("Tournament{0}Scenes", scenery.SceneName);
    }

    IEnumerable<string> GetTimeLimitedContentBundlesNames(string eventDataName)
    {
        yield return eventDataName + "Scenes";
    }

    IEnumerable<string> GetPvpContentBundlesNames()
    {
        yield return "PvpScenes";
    }

    IEnumerable<string> GetPrimaryRepetitionBundles(RegionData region)
    {
        foreach (var mi in region.RepetitionLevels[0].GetMissionInfos())
        {
            yield return "SharedScene" + mi.SceneName + "Scenery" + region.SceneName;
        }
    }

    IEnumerable<string> GetRegionContentBundlesNames(RegionData region, LevelData level)
    {
        yield return "RegionPictures" + region.name;
        yield return "RegionScenes" + region.name;
    }

    IEnumerable<string> GetRegionPicturesBundlesNames (RegionData region)
    {
        yield return "RegionPictures" + region.name;
    }

    IEnumerable<string> GetScenerySharedScenesBundleNames()
    {
        yield return "ScenerySharedScenes";
    }

    IEnumerable<string> GetSecondaryLevelBundleNames(LevelData level, SceneryData scenery)
    {
        foreach (var mi in level.GetMissionInfos())
            yield return "SharedScene" + mi.SceneName + "Scenery" + scenery.SceneName;
    }

    IEnumerable<string> GetTimeLimitedBundleNames(SceneryData scenery, string eventDataName)
    {
        var sceneryName = scenery != null ? scenery.SceneName : null;
        foreach (var sceneryBundle in GetSceneryBundlesNames(sceneryName))
            yield return sceneryBundle;

        foreach (var bundle in GetTimeLimitedContentBundlesNames(eventDataName))
            yield return bundle;
    }

    IEnumerable<string> GetTournamentBundleNames(SceneryData scenery)
    {
        foreach (var sceneryBundle in GetSceneryBundlesNames(scenery.SceneName))
            yield return sceneryBundle;

        foreach (var tournamentBundle in GetTournamentContentBundlesNames(scenery))
            yield return tournamentBundle;
    }

    IEnumerable<string> GetPvpBundleNames(SceneryData scenery)
    {
        foreach (var sceneryBundle in GetSceneryBundlesNames(scenery.SceneName))
            yield return sceneryBundle;

        foreach (var pvpBundle in GetPvpContentBundlesNames())
            yield return pvpBundle;
    }

    IEnumerable<string> GetRegionBundlesNames(RegionData region, LevelData level)
    {
        var scenery = region.SceneName;
        foreach (var sceneryBundle in GetSceneryBundlesNames(scenery))
            yield return sceneryBundle;

        foreach (var levelBundle in GetPrimaryRepetitionBundles(region))
            yield return levelBundle;

        foreach (var regionBundle in GetRegionContentBundlesNames(region,level))
            yield return regionBundle;
    }

    public bool CacheRegion(RegionData region)
    {
//        Debug.Log("Cache "+region.name);
        #if UNITY_EDITOR && !EDITOR_INCLUDED_BUNDLES
        return true;
        #else
        if (!HasBundles)
            return false;

        bool ret = true;

        foreach (var bundle in GetRegionBundles(region, null))
            ret &= Cache(bundle);

        return ret;
        #endif
    }

    public void UncacheRegion(RegionData region)
    {
        if(region.IsEmpty)
            return;
        #if UNITY_EDITOR && !EDITOR_INCLUDED_BUNDLES
        return;
        #elif !UNITY_WEBPLAYER

       // Debug.Log("Try Uncaching "+region.name);

        if(!Directory.Exists(CachePath))
            return;

        if (!HasBundles || region.IncludedInBuild)
            return ;

  //      Debug.Log("Uncaching "+region.name);


        var bundlesToDelete = new List<string>();
        foreach (var bundle in GetBundlesByName( GetRegionContentBundlesNames(region, null)))
        {
          //  Debug.Log("To Delete: "+bundle.Name);
            bundlesToDelete.Add(BundleCode(bundle));
            this[bundle].Unload();
        }

        var regionsKept = App.User.Regions.Where(r => r.ShouldKeepCached);

        TryUncacheScenery(region.Scenery);

        var secondaryLevels = region.DailyLevels.Concat(region.RepetitionLevels);
        foreach (var level in secondaryLevels)
        {
            var keptScenes = regionsKept.SelectMany(r => r.SecondaryLevels).SelectMany(l => l.GetMissionInfos()).Select(mi => mi.SceneName);
            var levelScenes = level.GetMissionInfos().Select(mi => mi.SceneName);

            bool shouldUncacheLevel = keptScenes.Intersect(levelScenes).None();
            if (shouldUncacheLevel)
            {
                foreach (var bundle in GetScenerySecondaryScenesBundles(level, region.Scenery))
                {
//                     Debug.Log("To Delete: "+bundle.Name);
                    bundlesToDelete.Add(BundleCode(bundle));
                    this[bundle].Unload();
                }
            }
        }

        var bundles = Directory.GetDirectories(CachePath);

        foreach(var bundlePath in bundles)
        {
            var bundleName = bundlePath.Split('/').Last();

            if(bundlesToDelete.Contains(bundleName))
            {
             //   Debug.Log("Delete "+bundleName);
                Directory.Delete(bundlePath,true);
            }
        }
        #endif
    }

    public void UncacheEvent(TimeLimitedEventData ev)
    {
        #if UNITY_EDITOR && !EDITOR_INCLUDED_BUNDLES
        return;
        #elif !UNITY_WEBPLAYER

        if (!Directory.Exists(CachePath))
            return;

        if (!HasBundles)
            return;

        var bundlesToDelete = new List<string>();
        foreach (var bundle in GetBundlesByName(GetTimeLimitedContentBundlesNames(ev.name)))
        {
            bundlesToDelete.Add(BundleCode(bundle));
            this[bundle].Unload();
        }

        TryUncacheScenery(ev.Scenery);

        DeleteBundlesByCode(bundlesToDelete);
        #endif
    }

    public void UncacheTournament(TournamentWeekData week)
    {
        #if UNITY_EDITOR && !EDITOR_INCLUDED_BUNDLES
        return;
        #elif !UNITY_WEBPLAYER

        if (!Directory.Exists(CachePath))
            return;

        if (!HasBundles)
            return;

        var bundlesToDelete = new List<string>();
        foreach (var bundle in GetBundlesByName(GetTournamentContentBundlesNames(week.Scenery)))
        {
            bundlesToDelete.Add(BundleCode(bundle));
            this[bundle].Unload();
        }

        TryUncacheScenery(week.Scenery);

        DeleteBundlesByCode(bundlesToDelete);
        #endif
    }

    void TryUncacheScenery(SceneryData scenery)
    {
        if (scenery == null) return;

        var events = App.TimeLimitedEventInteractor;
        var regionsKept = App.User.Regions.Where(r => r.ShouldKeepCached);
        var shouldUncacheScenery = regionsKept.None(r => r.Scenery == scenery)
            && !events.IsSceneryBeingUsed(scenery) && (scenery != App.PvpManager.Scenery) && (scenery != App.TournamentManager.CurrentWeekScenery);

        var bundlesToDelete = new List<string>();
        if (shouldUncacheScenery)
        {
            foreach (var bundle in GetBundlesByName(GetSceneryBundlesNames(scenery.SceneName)))
            {
                bundlesToDelete.Add(BundleCode(bundle));
                this[bundle].Unload();
            }
        }
        DeleteBundlesByCode(bundlesToDelete);
    }

    public void UncacheBundles(IEnumerable<Manifest.Bundle> bundles)
    {
#if UNITY_EDITOR && !EDITOR_INCLUDED_BUNDLES
        return;
        #elif !UNITY_WEBPLAYER

        if (!Directory.Exists(CachePath))
            return;

        if (!HasBundles)
            return;

        var bundlesToDelete = new List<string>();
        foreach (var bundle in bundles)
        {
            bundlesToDelete.Add(BundleCode(bundle));
            this[bundle].Unload();
        }

        DeleteBundlesByCode(bundlesToDelete);
#endif
    }

    void DeleteBundlesByCode(ICollection<string> bundleCodes)
    {
        var bundles = Directory.GetDirectories(CachePath);

        foreach (var bundlePath in bundles)
        {
            var bundleName = bundlePath.Split('/').Last();

            if (bundleCodes.Contains(bundleName))
            {
                Directory.Delete(bundlePath, true);
            }
        }
    }

    public bool RequestRegion(RegionData region, LevelData level)
    {
        #if UNITY_EDITOR && !EDITOR_INCLUDED_BUNDLES
        return true;
        #else
        if (!HasBundles)
            return false;

        bool ret = true;

        foreach (var bundle in GetRegionBundles(region, level))
        {
            ret &= Request(bundle);
        }

        return ret;
        #endif
    }

    public bool RequestRegionPictures (RegionData region)
    {
#if UNITY_EDITOR && !EDITOR_INCLUDED_BUNDLES
        return true;
#else
        if (!HasBundles)
            return false;

        bool ret = true;

        foreach (var bundle in GetRegionPicturesBundles(region))
        {
            ret &= Request(bundle);
        }

        return ret;
#endif
    }

    public bool IsRegionCached(RegionData region)
    {
        #if UNITY_EDITOR && !EDITOR_INCLUDED_BUNDLES
        return true;
        #else
        if (!HasBundles)
            return false;

        foreach (var bundle in GetRegionBundles(region, null))
        {
            if (!this[bundle].IsCached)
                return false;
        }
        return true;
        #endif
    }

    public bool IsSceneryCached(string sceneName)
    {
        #if UNITY_EDITOR && !EDITOR_INCLUDED_BUNDLES
        return true;
        #else
        if (!HasBundles)
            return false;

        foreach (var bundle in GetBundlesByName(GetSceneryBundlesNames (sceneName)))
        {
            if (!this[bundle].IsCached)
                return false;
        }
        return true;
        #endif
    }

    public bool IsRegionReady(RegionData region, LevelData level)
    {
        #if UNITY_EDITOR && !EDITOR_INCLUDED_BUNDLES
        return true;
        #else
        if (!HasBundles)
            return false;

        foreach (var bundle in GetRegionBundles(region, level))
        {
            if (!this[bundle].IsReady)
                return false;
        }
        return true;
        #endif
    }

    public BundlesDownloadState GetRegionDownloadState(RegionData region)
    {
        return GetDownloadState(GetRegionBundles(region, null));
    }
    #endregion

    #region Tournament

    public bool CacheTournament(SceneryData scenery)
    {
        return Cache(GetTournamentBundles(scenery));
    }

    public bool RequestTournament(SceneryData scenery)
    {
        return Request(GetTournamentBundles(scenery));
    }

    public bool IsTournamentCached(SceneryData scenery)
    {
        return IsCached(GetTournamentBundles(scenery));
    }

    public bool IsTournamentReady(SceneryData scenery)
    {
        return IsReady(GetTournamentBundles(scenery));
    }

    public BundlesDownloadState GetTournamentDownloadState(SceneryData scenery)
    {
        return GetDownloadState(GetTournamentBundles(scenery));
    }

    public BundlesDownloadState GetPvpDownloadState(SceneryData scenery)
    {
        return GetDownloadState(GetPvpBundles(scenery));
    }
    #endregion

    public bool Cache(IEnumerable<Manifest.Bundle> bundles)
    {
        #if UNITY_EDITOR && !EDITOR_INCLUDED_BUNDLES
        return true;
        #else
        if (!HasBundles)
            return false;

        bool ret = true;

        foreach (var bundle in bundles)
            ret &= Cache(bundle);

        return ret;
        #endif
    }

    public bool IsReady(IEnumerable<Manifest.Bundle> bundles)
    {
        #if UNITY_EDITOR && !EDITOR_INCLUDED_BUNDLES
        return true;
        #else
        if (!HasBundles)
            return false;

        foreach (var bundle in bundles)
        {
            if (!this[bundle].IsReady)
                return false;
        }
        return true;
        #endif
    }

    public bool Request(IEnumerable<Manifest.Bundle> bundles)
    {
        #if UNITY_EDITOR && !EDITOR_INCLUDED_BUNDLES
        return true;
        #else
        if (!HasBundles)
            return false;

        bool ret = true;

        foreach (var bundle in bundles)
            ret &= Request(bundle);

        return ret;
        #endif
    }

    public bool IsCached(IEnumerable<Manifest.Bundle> bundles)
    {
        #if UNITY_EDITOR && !EDITOR_INCLUDED_BUNDLES
        return true;
        #else
        if (!HasBundles)
            return false;

        foreach (var bundle in bundles)
        {
            if (!this[bundle].IsCached)
                return false;
        }
        return true;
        #endif
    }

    public bool IsScenerySharedScenesReady()
    {
        return IsReady(GetScenerySharedScenesBundles());
    }

    public BundlesDownloadState GetScenerySharedScenesDownloadState()
    {
        return GetDownloadState(GetScenerySharedScenesBundles());
    }


    #region PVP / Multiplayer

//    public bool CachePvp(SceneryData scenery)
//    {
//        return Cache(GetPvpBundles(scenery));
//    }
//
//    public bool RequestPvp(SceneryData scenery)
//    {
//        return Request(GetPvpBundles(scenery));
//    }
//
//    public bool IsPvpCached(SceneryData scenery)
//    {
//        return IsCached(GetPvpBundles(scenery));
//    }
//
//    public bool IsPvpBundleReady(SceneryData scenery)
//    {
//        return IsReady(GetPvpBundles(scenery));
//    }

    #endregion

    #region Mods
    List<Manifest.Bundle> _modsBundle;

    public IEnumerable<Manifest.Bundle> GetModsBundle()
    {
        if (_modsBundle == null && HasBundles)
            _modsBundle = GetBundlesByName(App.ModManager.ModsBundleName).ToList();
        return _modsBundle;
    }

    public BundlesDownloadState GetModsDownloadState() { return GetDownloadState(GetModsBundle()); }

    public bool CacheMods() { return Cache(GetModsBundle()); }
    public bool RequestMods() { return Request(GetModsBundle()); }
    public bool IsModsCached() { return IsCached(GetModsBundle()); }
    public bool IsModsBundleReady() { return IsReady(GetModsBundle()); }
    #endregion

    #region Popups

    public Manifest.Bundle GetPopupBundle(string popupName)
    {
        return HasBundles ? _manifest.Get(BundleNames.PopupBundle(popupName)) : null;
    }

    public bool CachePopup(string popupName)
    {
        var bundle = GetPopupBundle(popupName);
        return bundle != null && Cache(bundle);
    }

    public GameObject RequestOneTimeOfferPopup(string popupName)
    {
        return LoadAsset<GameObject>(popupName, GetPopupBundle(popupName));
    }

    #endregion

    #region Gear / Equipment

    public IEnumerable<Manifest.Bundle> GetEquipmentBundle()
    {
        var gearBundle = GetBundlesByName(App.GearManager.BundleName).ToList();
        return gearBundle;
    }

    #endregion

    public Manifest.Bundle GetBundle(string bundleName)
    {
        #if (!UNITY_EDITOR || EDITOR_INCLUDED_BUNDLES) && !RETAIL_BUILD
        Assert.IsTrue(HasBundles, string.Format("Trying to get bundle name {0} before bundles were ready", bundleName));
        var bundle = _manifest.Get(bundleName);
        Assert.IsNotNull(bundle, string.Format("Bundle name {0} returned a null bundle", bundleName));
        Assert.IsTrue(HasBundle(bundle), string.Format("Bundle name {0} is absent from manifest", bundleName));
        return bundle;
        #else
        return HasBundles ? _manifest.Get(bundleName) : null;
        #endif
    }
}
