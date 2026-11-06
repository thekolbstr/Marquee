// Lints the web client. The scripts are plain (non-module) files that share globals, so the globals below are declared once, here.
const browser = {
  window: 'readonly', document: 'readonly', navigator: 'readonly', location: 'readonly', history: 'readonly', localStorage: 'readonly', fetch: 'readonly', URLSearchParams: 'readonly',
  Blob: 'readonly', DOMParser: 'readonly', AbortController: 'readonly', IntersectionObserver: 'readonly', MouseEvent: 'readonly', KeyboardEvent: 'readonly', PointerEvent: 'readonly', Event: 'readonly',
  setTimeout: 'readonly', clearTimeout: 'readonly', setInterval: 'readonly', clearInterval: 'readonly', matchMedia: 'readonly', confirm: 'readonly', prompt: 'readonly', alert: 'readonly',
  console: 'readonly', CSS: 'readonly', Response: 'readonly', self: 'readonly', caches: 'readonly', clients: 'readonly', URL: 'readonly', innerHeight: 'readonly', module: 'readonly',
  Hls: 'readonly', qrcode: 'readonly', cast: 'readonly', chrome: 'readonly',
};
// Top-level names the scripts share (each defined in exactly one file; see index.html for the load order).
const shared = Object.fromEntries(['h', 'icon', 'api', 'toast', 'store', 'sleep', 'setView', 'view', '$', 'route', 'onLeave', 'runLeave', 'itemTile', 'tile', 'art', 'row', 'posterShare', 'POSTER_MIN', 'resLabel', 'codecLabel',
  'fmtRuntime', 'fmtLeft', 'fmtBytes', 'fmtUp', 'hashHue', 'capsQuery', 'CAPS', 'IS_SAFARI', 'watchScan', 'scanBtn', 'mountSystem', 'playerToken', 'activePlayback', 'teardownPlayer', 'showError',
  'AUTH', 'APP', 'isAdmin', 'authLost', 'signOut', 'showLogin', 'showSetup', 'showWelcome', 'applyServerName', 'refreshApp', 'paintUserChip', 'postJson', 'passwordField', 'greetingName',
  'pref', 'setPref', 'applyPrefs', 'skipSeconds', 'showSettings', 'toggle', 'field', 'folderManager', 'openFolderPicker',
  'inMyList', 'myListButton', 'showMyList', 'watchedMenu', 'showRecordings', 'startRecording', 'showPlayer', 'Player', 'loadLiveStatus', 'liveTile', 'showLive', 'showLivePlayer', 'reportClientError',
  'langKey', 'formatClock', 'formatRuntime', 'formatLeft', 'formatBytes', 'formatBitrate', 'bufferAhead', 'trickplayCell', 'qualitiesFor', 'pickCaption', 'audioLabel', 'codecName', 'statsRows', 'QUALITIES',
  'LANG_NAMES', 'LANG_ALIASES', 'newController', 'reportIssue', 'watchDecoding', 'itemHref', 'GUIDE_HOURS', 'GUIDE_PX', 'CHAN_W', 'liveInfo', 'itemSub', 'continueTile', 'loadLiveChannelsForHome', 'heroCarousel'].map((n) => [n, 'readonly']));
export default [{
  files: ['src/MediaServer.Api/wwwroot/*.js'],
  ignores: ['src/MediaServer.Api/wwwroot/vendor/**'],
  languageOptions: { ecmaVersion: 2023, sourceType: 'script', globals: { ...browser, ...shared } },
  rules: {
    'no-undef': 'error', 'no-unreachable': 'error', 'no-dupe-keys': 'error', 'no-dupe-args': 'error', 'no-duplicate-case': 'error', 'no-self-assign': 'error', 'no-const-assign': 'error',
    'no-redeclare': 'off', 'no-func-assign': 'error', 'no-unsafe-negation': 'error', 'use-isnan': 'error', 'valid-typeof': 'error', 'no-cond-assign': ['error', 'except-parens'],
    'no-unused-labels': 'error', 'no-sparse-arrays': 'error', 'no-empty-character-class': 'error', 'eqeqeq': ['error', 'always', { null: 'ignore' }], 'no-var': 'error', 'prefer-const': 'off',
  },
}, {
  files: ['web-tests/*.js'],
  languageOptions: { ecmaVersion: 2023, sourceType: 'commonjs', globals: { require: 'readonly', module: 'readonly', process: 'readonly' } },
}];
