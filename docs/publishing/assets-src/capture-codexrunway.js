const path = require('path');
const { chromium } = require('playwright');

const chrome = 'C:\\Program Files\\Google\\Chrome\\Application\\chrome.exe';
const output = path.join(__dirname, 'codexrunway-zh-2026-08-10.png');

(async () => {
  const browser = await chromium.launch({
    executablePath: chrome,
    headless: true,
    args: ['--disable-gpu'],
  });
  try {
    const page = await browser.newPage({
      viewport: { width: 1440, height: 1100 },
      deviceScaleFactor: 1,
      locale: 'zh-CN',
    });
    await page.goto('https://www.codexrunway.com/zh.html', {
      waitUntil: 'domcontentloaded',
      timeout: 90000,
    });
    await page.locator('body').waitFor({ state: 'visible', timeout: 30000 });
    await page.waitForTimeout(4000);
    await page.screenshot({ path: output, type: 'png', fullPage: false });
    console.log(`title=${await page.title()}`);
    console.log(`url=${page.url()}`);
    console.log(`output=${output}`);
    console.log((await page.locator('body').innerText()).replace(/\s+/g, ' ').slice(0, 800));
  } finally {
    await browser.close();
  }
})().catch((error) => {
  console.error(error);
  process.exitCode = 1;
});
