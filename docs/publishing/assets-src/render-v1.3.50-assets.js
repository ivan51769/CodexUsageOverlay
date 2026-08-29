const { execFileSync } = require('child_process');
const fs = require('fs');
const path = require('path');
const { pathToFileURL } = require('url');

const root = path.resolve(__dirname, '..', '..', '..');
const chrome = 'C:\\Program Files\\Google\\Chrome\\Application\\chrome.exe';
const source = pathToFileURL(path.join(__dirname, 'v1.3.50-cards.html')).href;
const jobs = [
  ['wechat-cover', path.join(root, 'docs', 'publishing', 'wechat-v1.3.50', 'assets', 'cover-1175x500.png'), 1175, 500],
  ['wechat-square', path.join(root, 'docs', 'publishing', 'wechat-v1.3.50', 'assets', 'cover-square-1080x1080.png'), 1080, 1080],
  ['wechat-01', path.join(root, 'docs', 'publishing', 'wechat-v1.3.50', 'assets', '01-direct-choice-1080x1440.png'), 1080, 1440],
  ['wechat-02', path.join(root, 'docs', 'publishing', 'wechat-v1.3.50', 'assets', '02-layouts-1080x1440.png'), 1080, 1440],
  ['wechat-03', path.join(root, 'docs', 'publishing', 'wechat-v1.3.50', 'assets', '03-theme-contrast-1080x1440.png'), 1080, 1440],
  ['wechat-04', path.join(root, 'docs', 'publishing', 'wechat-v1.3.50', 'assets', '04-boundary-1080x1440.png'), 1080, 1440],
  ['wechat-05', path.join(root, 'docs', 'publishing', 'wechat-v1.3.50', 'assets', '05-one-line-matrix-1080x1440.png'), 1080, 1440],
  ['wechat-06', path.join(root, 'docs', 'publishing', 'wechat-v1.3.50', 'assets', '06-two-lines-matrix-1080x1440.png'), 1080, 1440],
  ['xhs-01', path.join(root, 'docs', 'publishing', 'xiaohongshu', 'assets-v1.3.50', '01-cover-1080x1440.png'), 1080, 1440],
  ['xhs-02', path.join(root, 'docs', 'publishing', 'xiaohongshu', 'assets-v1.3.50', '02-direct-choice-1080x1440.png'), 1080, 1440],
  ['xhs-03', path.join(root, 'docs', 'publishing', 'xiaohongshu', 'assets-v1.3.50', '03-theme-contrast-1080x1440.png'), 1080, 1440],
  ['xhs-04', path.join(root, 'docs', 'publishing', 'xiaohongshu', 'assets-v1.3.50', '04-boundary-1080x1440.png'), 1080, 1440],
  ['xhs-05', path.join(root, 'docs', 'publishing', 'xiaohongshu', 'assets-v1.3.50', '05-one-line-matrix-1080x1440.png'), 1080, 1440],
  ['xhs-06', path.join(root, 'docs', 'publishing', 'xiaohongshu', 'assets-v1.3.50', '06-two-lines-matrix-1080x1440.png'), 1080, 1440],
];

for (const [card, output, width, height] of jobs) {
  fs.mkdirSync(path.dirname(output), { recursive: true });
  const url = `${source}?card=${encodeURIComponent(card)}`;
  execFileSync(chrome, [
    '--headless=new', '--disable-gpu', '--hide-scrollbars', '--force-device-scale-factor=1',
    '--allow-file-access-from-files', '--run-all-compositor-stages-before-draw',
    `--window-size=${width},${height}`, `--screenshot=${output}`, url,
  ], { stdio: 'pipe' });
  console.log(`${card}\t${width}x${height}\t${output}`);
}
