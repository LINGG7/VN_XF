mergeInto(LibraryManager.library, {
  $VNovelizer_TapGalleryAudio: {
    warned: false,
    runtime: function () {
      var root = typeof GameGlobal !== "undefined" ? GameGlobal : null;
      var runtime = root && root.VNovelizerTapGalleryAudio;
      if (!runtime && root && root.managerConfig && !this.warned) {
        this.warned = true;
        console.warn("[TapGalleryAudio] v4 boot module missing; re-export the minigame with the VNovelizer conversion hook.");
      }
      return runtime;
    },
    begin: function (time) { var runtime = this.runtime(); return runtime ? runtime.begin(time) : 0; },
    end: function () { var runtime = this.runtime(); if (runtime) runtime.end(); },
    position: function () { var runtime = this.runtime(); return runtime ? runtime.position() : -1; },
    reset: function () { var runtime = this.runtime(); if (runtime) runtime.reset(); }
  },
  VNovelizer_TapGalleryBeginPlayback__deps: ["$VNovelizer_TapGalleryAudio"],
  VNovelizer_TapGalleryBeginPlayback: function (time) { return VNovelizer_TapGalleryAudio.begin(time); },
  VNovelizer_TapGalleryEndPlayback__deps: ["$VNovelizer_TapGalleryAudio"],
  VNovelizer_TapGalleryEndPlayback: function () { VNovelizer_TapGalleryAudio.end(); },
  VNovelizer_TapGalleryPlaybackPosition__deps: ["$VNovelizer_TapGalleryAudio"],
  VNovelizer_TapGalleryPlaybackPosition: function () { return VNovelizer_TapGalleryAudio.position(); },
  VNovelizer_TapGalleryResetPlayback__deps: ["$VNovelizer_TapGalleryAudio"],
  VNovelizer_TapGalleryResetPlayback: function () { VNovelizer_TapGalleryAudio.reset(); }
});