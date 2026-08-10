const path = require('path');
const { pathToFileURL } = require('url');
const { chromium } = require('playwright');
const sharp = require('sharp');

const root = path.resolve(__dirname, '..', '..', '..');
const chrome = 'C:\\Program Files\\Google\\Chrome\\Application\\chrome.exe';
const outputs = [
  ['readme-overview', path.join(root, 'docs', 'images', 'features', 'reset-radar-overview.png'), 900, 120],
  ['wechat-cover', path.join(root, 'docs', 'publishing', 'wechat', 'assets', 'cover-1175x500.png'), 1175, 500],
  ['wechat-cover-square', path.join(root, 'docs', 'publishing', 'wechat', 'assets', 'cover-square-1080x1080.png'), 1080, 1080],
  ['wechat-01-overview', path.join(root, 'docs', 'publishing', 'wechat', 'assets', '01-overview-1080x1440.png'), 1080, 1440],
  ['wechat-02-radar', path.join(root, 'docs', 'publishing', 'wechat', 'assets', '02-tibo-radar-1080x1440.png'), 1080, 1440],
  ['wechat-03-themes', path.join(root, 'docs', 'publishing', 'wechat', 'assets', '03-themes-1080x1440.png'), 1080, 1440],
  ['wechat-04-privacy', path.join(root, 'docs', 'publishing', 'wechat', 'assets', '04-data-boundary-1080x1440.png'), 1080, 1440],
  ['wechat-05-source', path.join(root, 'docs', 'publishing', 'wechat', 'assets', '05-codex-runway-source-1080x1440.png'), 1080, 1440],
  ['xhs-01-cover', path.join(root, 'docs', 'publishing', 'xiaohongshu', 'assets', '01-cover-1080x1440.png'), 1080, 1440],
  ['xhs-02-radar', path.join(root, 'docs', 'publishing', 'xiaohongshu', 'assets', '02-tibo-radar-1080x1440.png'), 1080, 1440],
  ['xhs-03-themes', path.join(root, 'docs', 'publishing', 'xiaohongshu', 'assets', '03-themes-1080x1440.png'), 1080, 1440],
  ['xhs-04-boundary', path.join(root, 'docs', 'publishing', 'xiaohongshu', 'assets', '04-boundary-1080x1440.png'), 1080, 1440],
];

(async () => {
  const browser = await chromium.launch({
    executablePath: chrome,
    headless: true,
    args: ['--allow-file-access-from-files', '--disable-gpu'],
  });
  try {
    const page = await browser.newPage({ viewport: { width: 1300, height: 1600 }, deviceScaleFactor: 1 });
    await page.goto(pathToFileURL(path.join(__dirname, 'cards.html')).href, { waitUntil: 'load' });
    await page.evaluate(() => document.fonts.ready);
    for (const [id, output, width, height] of outputs) {
      const artboard = page.locator(`#${id}`);
      const screenshot = await artboard.screenshot({ type: 'png', animations: 'disabled' });
      await sharp(screenshot).extract({ left: 0, top: 0, width, height }).png().toFile(output);
      console.log(`${id}\t${output}`);
    }
  } finally {
    await browser.close();
  }
})().catch((error) => {
  console.error(error);
  process.exitCode = 1;
});
