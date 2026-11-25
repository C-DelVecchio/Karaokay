#!/usr/bin/env node
/**
 * Deterministic yt-dlp stand-in for the Playwright suite (Slice 5 ingestion).
 *
 * The e2e API is started with YtDlp__ExecutablePath pointing at this file. It uses only Node
 * built-ins, talks to no network, and implements exactly the two invocations YtDlpClient makes:
 *
 *  - search  (`--dump-single-json`): prints one collapsed playlist document with a known video id.
 *  - download (`-o <template> --newline`): writes the committed base64 sample MP4 (a real H.264
 *    file, stored as text so ADR-004's binary-media guard is satisfied) to the requested output
 *    path and emits the same `karaokay-progress:` lines yt-dlp's progress template would.
 */
import { mkdirSync, readFileSync, writeFileSync } from 'node:fs';
import { dirname } from 'node:path';
import { fileURLToPath } from 'node:url';

const VIDEO_ID = 'e2eTestVid1';
const VIDEO_TITLE = 'E2E Karaoke Fixture';
const VIDEO_CHANNEL = 'Karaokay CI';
const VIDEO_DURATION_SECONDS = 120;
const PROGRESS_DELAY_MS = 400;

const args = process.argv.slice(2);

if (args.includes('--dump-single-json')) {
  // A 1x1 transparent PNG keeps the result thumbnail data-only (no network from the browser).
  const thumbnail =
    'data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg==';
  const document = {
    _type: 'playlist',
    id: `ytsearch:${args.at(-1) ?? 'fixture'}`,
    title: 'karaokay e2e fixture',
    entries: [
      {
        _type: 'url',
        ie_key: 'Youtube',
        id: VIDEO_ID,
        url: `https://www.youtube.com/watch?v=${VIDEO_ID}`,
        title: VIDEO_TITLE,
        channel: VIDEO_CHANNEL,
        uploader: VIDEO_CHANNEL,
        duration: VIDEO_DURATION_SECONDS,
        thumbnails: [{ url: thumbnail }],
      },
    ],
  };

  process.stdout.write(JSON.stringify(document) + '\n');
  process.exit(0);
}

const outputIndex = args.indexOf('-o');
if (outputIndex < 0 || outputIndex + 1 >= args.length) {
  process.stderr.write('yt-dlp-shim: download invocation is missing -o <template>\n');
  process.exit(2);
}

const outputTemplate = args[outputIndex + 1];
const outputPath = outputTemplate.replace('%(ext)s', 'mp4');
const samplePath = fileURLToPath(
  new URL('../../../../tests/fixtures/youtube-sample.mp4.b64', import.meta.url),
);
const bytes = Buffer.from(readFileSync(samplePath, 'utf8').trim(), 'base64');
const total = bytes.length;

mkdirSync(dirname(outputPath), { recursive: true });
writeFileSync(outputPath, bytes);

// Emit the numeric progress-template contract, one sample per line (--newline).
for (const percent of [25, 50, 75, 100]) {
  const downloaded = Math.round((total * percent) / 100);
  process.stdout.write(
    `karaokay-progress:${downloaded}|${total}|${total}|1048576|1\n`,
  );
  await new Promise((resolve) => setTimeout(resolve, PROGRESS_DELAY_MS));
}

process.exit(0);
