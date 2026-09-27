import { defineConfig } from 'tsup';

export default defineConfig({
  entry: ['src/index.ts'],
  format: ['cjs', 'esm'],
  dts: true,
  clean: true,
  sourcemap: true,
  target: 'es2022',
  // React Server Components convention: every entry point in this package uses hooks/context,
  // so the whole bundle is client-only.
  banner: {
    js: '"use client";'
  },
  external: ['react', '@dotwire/client']
});
