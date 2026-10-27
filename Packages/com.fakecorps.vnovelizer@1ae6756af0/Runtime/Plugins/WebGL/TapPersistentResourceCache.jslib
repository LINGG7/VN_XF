mergeInto(LibraryManager.library, {
  $VNovelizer_TapBundleCache: {
    operations: {},
    nextToken: 1,
    activeSdk: null,
    activeSdkName: "",
    lastUnavailableReason: "",
    candidates: function () {
      var result = [];
      function add(name, sdk) {
        if (sdk && sdk.env && sdk.env.USER_DATA_PATH && typeof sdk.getFileSystemManager === "function")
          result.push({ name: name, sdk: sdk });
      }
      if (typeof tap !== "undefined") add("tap", tap);
      if (typeof tj !== "undefined") add("tj", tj);
      if (typeof tjh !== "undefined") add("tjh", tjh);
      if (typeof GameGlobal !== "undefined") {
        add("GameGlobal.tap", GameGlobal.tap);
        add("GameGlobal.tj", GameGlobal.tj);
        add("GameGlobal.tjh", GameGlobal.tjh);
      }
      return result;
    },
    isMiniGame: function () {
      return this.candidates().length > 0 || (typeof GameGlobal !== "undefined" &&
        !!GameGlobal.managerConfig && !!GameGlobal.unityNamespace);
    },
    api: function () {
      var candidates = this.candidates();
      var required = ["accessSync", "mkdirSync", "statSync", "readFileSync", "writeFileSync", "renameSync", "unlinkSync", "readdirSync"];
      this.activeSdk = null;
      this.lastUnavailableReason = "No supported minigame file API";
      for (var n = 0; n < candidates.length; n++) {
        var candidate = candidates[n];
        try {
          if (typeof candidate.sdk.downloadFile !== "function") {
            this.lastUnavailableReason = candidate.name + ".downloadFile is unavailable";
            continue;
          }
          var fs = candidate.sdk.getFileSystemManager();
          var missing = [];
          for (var i = 0; i < required.length; i++)
            if (!fs || typeof fs[required[i]] !== "function") missing.push(required[i]);
          if (missing.length) {
            this.lastUnavailableReason = candidate.name + " file API lacks: " + missing.join(", ");
            continue;
          }
          this.activeSdk = candidate.sdk;
          this.activeSdkName = candidate.name;
          return fs;
        } catch (e) { this.lastUnavailableReason = candidate.name + ": " + this.error(e); }
      }
      return null;
    },
    root: function () {
      if (!this.api()) throw new Error("TapTap resource storage is unavailable");
      return this.activeSdk.env.USER_DATA_PATH + "/VNovelizer/ResourceCache";
    },
    path: function (key, suffix) {
      if (!/^[a-f0-9]{64}$/.test(key)) throw new Error("Invalid resource cache key");
      if (!/^(bundle|json|json\.part|[0-9]+\.part)$/.test(suffix)) throw new Error("Invalid resource cache suffix");
      return this.root() + "/" + key + "." + suffix;
    },
    ensureDirectory: function () {
      var fs = this.api();
      var dirs = [this.activeSdk.env.USER_DATA_PATH + "/VNovelizer", this.root()];
      for (var i = 0; i < dirs.length; i++) {
        try { fs.accessSync(dirs[i]); } catch (e) { fs.mkdirSync(dirs[i]); }
      }
    },
    size: function (path) {
      var stat = this.api().statSync(path);
      if (stat && stat.stats) stat = stat.stats;
      if (!stat || typeof stat.size !== "number") throw new Error("Resource storage stat failed");
      return stat.size;
    },
    remove: function (path) {
      var fs = this.api();
      try { fs.accessSync(path); } catch (e) {
        var message = this.error(e);
        if (/no such|not exist|ENOENT|1300002/i.test(message)) return;
        throw e;
      }
      fs.unlinkSync(path);
    },
    error: function (e) {
      return String(e && (e.errMsg || e.message) || e || "Resource download failed")
        .replace(/https?:\/\/[^\s]+/g, function (url) { return url.split("?")[0].split("#")[0]; });
    },
    bytes: function (data) {
      if (data && data.data) data = data.data;
      if (data instanceof ArrayBuffer) return new Uint8Array(data);
      if (ArrayBuffer.isView(data)) return new Uint8Array(data.buffer, data.byteOffset, data.byteLength);
      throw new Error("Resource storage did not return binary data");
    },
    invalidate: function (key) {
      this.remove(this.path(key, "json"));
      this.remove(this.path(key, "bundle"));
    },
    cached: function (key, expectedSize) {
      try {
        var manifest = JSON.parse(this.api().readFileSync(this.path(key, "json"), "utf8"));
        var actualSize = this.size(this.path(key, "bundle"));
        return manifest.version === 1 && actualSize > 0 && manifest.size === actualSize &&
          (expectedSize <= 0 || actualSize === expectedSize);
      } catch (e) { return false; }
    }
  },

  VNovelizer_TapBundleIsMiniGame__deps: ["$VNovelizer_TapBundleCache"],
  VNovelizer_TapBundleIsMiniGame: function () {
    try { return VNovelizer_TapBundleCache.isMiniGame() ? 1 : 0; } catch (e) { return 0; }
  },
  VNovelizer_TapBundleCacheAvailable__deps: ["$VNovelizer_TapBundleCache"],
  VNovelizer_TapBundleCacheAvailable: function () {
    var cache = VNovelizer_TapBundleCache;
    try {
      var available = !!cache.api();
      var diagnostic = available ? "native SDK=" + cache.activeSdkName : cache.lastUnavailableReason;
      if (cache.isMiniGame() && cache.lastDiagnostic !== diagnostic) {
        cache.lastDiagnostic = diagnostic;
        if (available) {
          if (typeof console.log === "function") console.log("[TapResourceCache] " + diagnostic);
        } else console.warn("[TapResourceCache] Native cache unavailable: " + diagnostic);
      }
      return available ? 1 : 0;
    } catch (e) { return 0; }
  },
  VNovelizer_TapBundleIsCached__deps: ["$VNovelizer_TapBundleCache"],
  VNovelizer_TapBundleIsCached: function (keyPtr, expectedSize) {
    return VNovelizer_TapBundleCache.cached(UTF8ToString(keyPtr), expectedSize) ? 1 : 0;
  },
  VNovelizer_TapBundleFileSize__deps: ["$VNovelizer_TapBundleCache"],
  VNovelizer_TapBundleFileSize: function (keyPtr) {
    try {
      var size = VNovelizer_TapBundleCache.size(VNovelizer_TapBundleCache.path(UTF8ToString(keyPtr), "bundle"));
      return size > 0 && size <= 2147483647 ? size : -1;
    } catch (e) { return -1; }
  },
  VNovelizer_TapBundleRead__deps: ["$VNovelizer_TapBundleCache"],
  VNovelizer_TapBundleRead: function (keyPtr, bufferPtr, length) {
    try {
      var cache = VNovelizer_TapBundleCache;
      var bytes = cache.bytes(cache.api().readFileSync(cache.path(UTF8ToString(keyPtr), "bundle")));
      if (bytes.length !== length || length <= 0 || !bufferPtr) return -1;
      HEAPU8.set(bytes, bufferPtr);
      return length;
    } catch (e) { return -1; }
  },
  VNovelizer_TapBundleBeginDownload__deps: ["$VNovelizer_TapBundleCache"],
  VNovelizer_TapBundleBeginDownload: function (urlPtr, keyPtr, timeoutMs) {
    var cache = VNovelizer_TapBundleCache;
    var token = cache.nextToken++;
    var operation = { state: 0, bytes: 0, error: "", task: null, part: "" };
    cache.operations[token] = operation;
    try {
      var url = UTF8ToString(urlPtr);
      var key = UTF8ToString(keyPtr);
      if (!/^https?:\/\//i.test(url)) throw new Error("Only remote resource URLs are allowed");
      cache.ensureDirectory();
      var destination = cache.path(key, "bundle");
      operation.part = cache.path(key, token + ".part");
      cache.remove(operation.part);
      // Only a successful Unity CRC check may commit this download as reusable.
      cache.remove(cache.path(key, "json"));
      operation.task = cache.activeSdk.downloadFile({
        url: url,
        filePath: operation.part,
        timeout: timeoutMs > 0 ? timeoutMs : 180000,
        success: function (result) {
          if (cache.operations[token] !== operation) {
            try { cache.remove(operation.part); } catch (e) { }
            return;
          }
          try {
            if (result.statusCode < 200 || result.statusCode >= 300)
              throw new Error("HTTP " + result.statusCode + " resource download failed");
            // Require the requested user-file path. A temporary-file-only response
            // must not be silently treated as a persistent cache entry.
            var size = cache.size(operation.part);
            if (size <= 0) throw new Error("Downloaded resource file is empty");
            cache.remove(destination);
            cache.api().renameSync(operation.part, destination);
            operation.bytes = size;
            operation.state = 1;
          } catch (e) { operation.state = -1; operation.error = cache.error(e); }
        },
        fail: function (e) {
          if (cache.operations[token] !== operation) return;
          operation.state = -1;
          operation.error = cache.error(e);
        }
      });
      if (operation.task && typeof operation.task.onProgressUpdate === "function")
        operation.task.onProgressUpdate(function (progress) {
          if (cache.operations[token] === operation) operation.bytes = progress.totalBytesWritten || 0;
        });
    } catch (e) { operation.state = -1; operation.error = cache.error(e); }
    return token;
  },
  VNovelizer_TapBundleDownloadState__deps: ["$VNovelizer_TapBundleCache"],
  VNovelizer_TapBundleDownloadState: function (token) {
    var operation = VNovelizer_TapBundleCache.operations[token];
    return operation ? operation.state : -1;
  },
  VNovelizer_TapBundleDownloadedBytes__deps: ["$VNovelizer_TapBundleCache"],
  VNovelizer_TapBundleDownloadedBytes: function (token) {
    var operation = VNovelizer_TapBundleCache.operations[token];
    return operation ? operation.bytes : 0;
  },
  VNovelizer_TapBundleDownloadError__deps: ["$VNovelizer_TapBundleCache"],
  VNovelizer_TapBundleDownloadError: function (token) {
    var operation = VNovelizer_TapBundleCache.operations[token];
    return stringToNewUTF8(operation ? operation.error : "Resource download operation ended");
  },
  VNovelizer_TapBundleEndDownload__deps: ["$VNovelizer_TapBundleCache"],
  VNovelizer_TapBundleEndDownload: function (token) {
    var cache = VNovelizer_TapBundleCache;
    var operation = cache.operations[token];
    if (!operation) return;
    delete cache.operations[token];
    if (operation.state === 0 && operation.task && typeof operation.task.abort === "function") operation.task.abort();
    if (operation.part) { try { cache.remove(operation.part); } catch (e) { } }
  },
  VNovelizer_TapBundleCommit__deps: ["$VNovelizer_TapBundleCache"],
  VNovelizer_TapBundleCommit: function (keyPtr, expectedSize) {
    try {
      var cache = VNovelizer_TapBundleCache;
      var key = UTF8ToString(keyPtr);
      var actualSize = cache.size(cache.path(key, "bundle"));
      if (actualSize <= 0 || (expectedSize > 0 && actualSize !== expectedSize)) return 0;
      var temp = cache.path(key, "json.part");
      var manifest = cache.path(key, "json");
      cache.api().writeFileSync(temp, JSON.stringify({ version: 1, size: actualSize }), "utf8");
      cache.remove(manifest);
      cache.api().renameSync(temp, manifest);
      return 1;
    } catch (e) {
      console.warn("[TapResourceCache] Storage commit failed:", VNovelizer_TapBundleCache.error(e));
      return 0;
    }
  },
  VNovelizer_TapBundleInvalidate__deps: ["$VNovelizer_TapBundleCache"],
  VNovelizer_TapBundleInvalidate: function (keyPtr) {
    VNovelizer_TapBundleCache.invalidate(UTF8ToString(keyPtr));
  },
  VNovelizer_TapBundleCacheSize__deps: ["$VNovelizer_TapBundleCache"],
  VNovelizer_TapBundleCacheSize: function () {
    try {
      var cache = VNovelizer_TapBundleCache;
      var files = cache.api().readdirSync(cache.root());
      var size = 0;
      for (var i = 0; i < files.length; i++)
        if (/^[a-f0-9]{64}\.bundle$/.test(files[i])) size += cache.size(cache.root() + "/" + files[i]);
      return size;
    } catch (e) { return -1; }
  },
  VNovelizer_TapBundleClearCache__deps: ["$VNovelizer_TapBundleCache"],
  VNovelizer_TapBundleClearCache: function () {
    try {
      var cache = VNovelizer_TapBundleCache;
      if (Object.keys(cache.operations).length !== 0) return 0;
      cache.ensureDirectory();
      var files = cache.api().readdirSync(cache.root());
      for (var i = 0; i < files.length; i++) {
        if (/^[a-f0-9]{64}\.(bundle|json|json\.part|[0-9]+\.part)$/.test(files[i]))
          cache.remove(cache.root() + "/" + files[i]);
      }
      return 1;
    } catch (e) { return 0; }
  },
  VNovelizer_TapBundleFree: function (ptr) { if (ptr) _free(ptr); }
});