#!/usr/bin/env node
/*
 * Capture the deterministic GlassDesk showcase scenes and encode GIF + poster PNGs.
 * Windows: install Node.js, make Puppeteer-core and ffmpeg available, then run from
 * the repository root. Set GLASSDESK_CHROME to Chrome or Edge's executable path:
 *   $env:GLASSDESK_CHROME = 'C:\Program Files\Google\Chrome\Application\chrome.exe'
 *   node docs/showcase/record.js
 * PUPPETEER_CORE_PATH and FFMPEG_PATH can override tool locations. The default
 * Puppeteer-core path is the prepared gifkit install used in this workspace.
 */
'use strict';
const fs = require('fs');
const path = require('path');
const { pathToFileURL } = require('url');
const { spawnSync } = require('child_process');
let puppeteer;
try {
  puppeteer = require(process.env.PUPPETEER_CORE_PATH || '/workspace/gd0213/gifkit/node_modules/puppeteer-core');
} catch (error) {
  puppeteer = require('puppeteer-core');
}
const root = path.resolve(__dirname, '../..');
const htmlPath = path.join(root, 'docs', 'index.html');
const imageDir = path.join(root, 'docs', 'images');
const workDir = path.join(require('os').tmpdir(), 'glassdesk-showcase-frames');
const ffmpeg = process.env.FFMPEG_PATH || 'ffmpeg';
const browserPath = process.env.GLASSDESK_CHROME || process.env.PUPPETEER_EXECUTABLE_PATH
  || process.env.CHROME_PATH || '/usr/bin/google-chrome';
const scenes = [
  { name: 'morph', width: 720, height: 450, fps: 15, seconds: 6 },
  { name: 'drag', width: 720, height: 450, fps: 15, seconds: 4 },
  { name: 'peek', width: 560, height: 320, fps: 15, seconds: 3 }
];
function run(command, args) {
  const result = spawnSync(command, args, { encoding: 'utf8', windowsHide: true });
  if (result.error) throw result.error;
  if (result.status !== 0) throw new Error((result.stderr || result.stdout || command).trim());
}
function encodeGif(inputPattern, output, width, height, fps) {
  const paletteSizes = [160, 112, 80, 56, 40];
  for (const colors of paletteSizes) {
    const filter = 'fps=' + fps + ',scale=' + width + ':' + height + ':flags=lanczos,split[s0][s1];'
      + '[s0]palettegen=max_colors=' + colors + ':stats_mode=diff[p];'
      + '[s1][p]paletteuse=dither=bayer:bayer_scale=3';
    run(ffmpeg, ['-hide_banner', '-loglevel', 'error', '-y', '-framerate', String(fps),
      '-i', inputPattern, '-filter_complex', filter, '-loop', '0', output]);
    if (fs.statSync(output).size <= 4 * 1024 * 1024) return colors;
  }
  return paletteSizes[paletteSizes.length - 1];
}
async function main() {
  fs.mkdirSync(imageDir, { recursive: true });
  fs.rmSync(workDir, { recursive: true, force: true });
  fs.mkdirSync(workDir, { recursive: true });
  const browser = await puppeteer.launch({
    executablePath: browserPath,
    headless: 'new',
    args: ['--no-sandbox', '--use-angle=swiftshader', '--enable-unsafe-swiftshader',
      '--enable-webgl', '--ignore-gpu-blocklist', '--disable-dev-shm-usage']
  });
  try {
    for (const scene of scenes) {
      const sceneDir = path.join(workDir, scene.name);
      fs.mkdirSync(sceneDir, { recursive: true });
      const page = await browser.newPage();
      page.setDefaultTimeout(180000);
      await page.setViewport({ width: scene.width, height: scene.height, deviceScaleFactor: 1 });
      page.on('pageerror', error => { throw error; });
      await page.goto(pathToFileURL(htmlPath).href + '?capture=' + scene.name, { waitUntil: 'load', timeout: 180000 });
      await page.waitForFunction(() => !!(window.GlassDeskShowcase && window.GlassDeskShowcase.status), { polling: 250, timeout: 180000 });
      await page.evaluate(() => document.fonts.ready);
      const state = await page.evaluate(() => window.GlassDeskShowcase.status());
      if (!state.webgl) throw new Error('WebGL2 initialization failed for scene ' + scene.name);
      const count = scene.fps * scene.seconds;
      for (let i = 0; i < count; i++) {
        const time = i / scene.fps;
        await page.evaluate((t, name) => window.GlassDeskShowcase.renderAt(t, name), time, scene.name);
        const framePath = path.join(sceneDir, 'frame-' + String(i).padStart(4, '0') + '.png');
        await page.screenshot({ path: framePath, type: 'png', clip: { x: 0, y: 0, width: scene.width, height: scene.height } });
        if (i === 0) fs.copyFileSync(framePath, path.join(imageDir, 'liquid-' + scene.name + '.png'));
      }
      await page.close();
      const gifPath = path.join(imageDir, 'liquid-' + scene.name + '.gif');
      const chosen = encodeGif(path.join(sceneDir, 'frame-%04d.png'), gifPath, scene.width, scene.height, scene.fps);
      const bytes = fs.statSync(gifPath).size;
      console.log(scene.name + ': ' + scene.width + 'x' + scene.height + ', ' + count + ' frames, '
        + scene.fps + ' fps, palette ' + chosen + ', ' + (bytes / 1024 / 1024).toFixed(2) + ' MiB');
      if (bytes > 4 * 1024 * 1024) console.warn('  GIF is over 4 MiB; lower palette size or reduce background motion.');
    }
  } finally {
    await browser.close();
  }
  fs.rmSync(workDir, { recursive: true, force: true });
}
main().catch(error => { console.error(error); process.exitCode = 1; });

