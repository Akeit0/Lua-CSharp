#!/usr/bin/env node
import { build, context } from 'esbuild';
import { mkdir, rm } from 'node:fs/promises';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const __filename = fileURLToPath(import.meta.url);
const __dirname = dirname(__filename);
const root = resolve(__dirname, '..');
const src = resolve(root, 'src');
const dist = resolve(root, 'dist');

const watch = process.argv.includes('--watch');

await rm(dist, { recursive: true, force: true });
await mkdir(dist, { recursive: true });

const options = {
  entryPoints: [resolve(src, 'extension.ts')],
  outfile: resolve(dist, 'extension.js'),
  bundle: true,
  platform: 'node',
  target: 'node20',
  sourcemap: true,
  external: ['vscode'],
};

if (watch) {
  const ctx = await context(options);
  console.log('[watch] build started');
  await ctx.rebuild();
  console.log('[watch] build finished');
  await ctx.watch();
  console.log('watching...');
} else {
  await build(options);
  console.log('[lua-csharp] build complete');
}
