import { defineConfig } from '@playwright/test';
export default defineConfig({
  testDir:'./browser-tests', timeout:60000, workers:1, reporter:[['list'],['json',{outputFile:'test-results/browser.json'}]],
  use:{browserName:'chromium', viewport:{width:412,height:915}, screenshot:'only-on-failure', trace:'off'},
});
