const fs = require('fs');
const path = require('path');
const puppeteer = require('puppeteer-core');

function getBrowserPath() {
  const custom = process.env.CHROME_PATH;
  if (custom && fs.existsSync(custom)) return custom;

  const candidates = [
    'C:\\Program Files\\Google\\Chrome\\Application\\chrome.exe',
    'C:\\Program Files (x86)\\Google\\Chrome\\Application\\chrome.exe',
    'C:\\Program Files (x86)\\Microsoft\\Edge\\Application\\msedge.exe',
    'C:\\Program Files\\Microsoft\\Edge\\Application\\msedge.exe'
  ];

  for (const c of candidates) {
    if (fs.existsSync(c)) return c;
  }
  throw new Error('Khong tim thay Google Chrome hoac Microsoft Edge tren may tinh.');
}

async function getCaptcha() {
  const browserPath = getBrowserPath();
  const browser = await puppeteer.launch({
    executablePath: browserPath,
    headless: 'new',
    args: ['--no-sandbox', '--disable-setuid-sandbox']
  });

  try {
    const page = await browser.newPage();
    await page.goto('https://hoadondientu.gdt.gov.vn', { waitUntil: 'networkidle2', timeout: 30000 });

    const result = await page.evaluate(async () => {
      const res = await fetch('/api/captcha');
      if (!res.ok) {
        return { success: false, message: `Loi HTTP ${res.status} khi lay captcha` };
      }
      const data = await res.json();
      return { success: true, key: data.key, content: data.content };
    });

    console.log(JSON.stringify(result));
  } finally {
    await browser.close();
  }
}

async function login(username, password, captcha, key) {
  const browserPath = getBrowserPath();
  const browser = await puppeteer.launch({
    executablePath: browserPath,
    headless: 'new',
    args: ['--no-sandbox', '--disable-setuid-sandbox']
  });

  try {
    const page = await browser.newPage();
    await page.goto('https://hoadondientu.gdt.gov.vn', { waitUntil: 'networkidle2', timeout: 30000 });

    const result = await page.evaluate(async (u, p, c, k) => {
      try {
        const res = await fetch('/api/security-taxpayer/authenticate', {
          method: 'POST',
          headers: {
            'Content-Type': 'application/json',
            'request-id': crypto.randomUUID(),
            'End-Point': '/',
            'Action': ''
          },
          body: JSON.stringify({
            username: u,
            password: p,
            cvalue: c,
            ckey: k
          })
        });

        const text = await res.text();
        let json = null;
        try { json = JSON.parse(text); } catch (_) {}

        if (res.ok) {
          const token = json?.token || json?.access_token || json?.jwt;
          if (token) {
            return { success: true, token };
          }
          return { success: false, message: 'Dang nhap thanh cong nhung khong tim thay token' };
        }

        const msg = json?.message || (text.includes('Request Rejected') ? 'Yeu cau bi Tuong lua chan' : text) || `Loi HTTP ${res.status}`;
        return { success: false, status: res.status, message: msg };
      } catch (err) {
        return { success: false, message: err.message };
      }
    }, username, password, captcha, key);

    console.log(JSON.stringify(result));
  } finally {
    await browser.close();
  }
}

async function fetchInvoices(token, endpoint, searchParam) {
  const browserPath = getBrowserPath();
  const browser = await puppeteer.launch({
    executablePath: browserPath,
    headless: 'new',
    args: ['--no-sandbox', '--disable-setuid-sandbox']
  });

  try {
    const page = await browser.newPage();
    await page.goto('https://hoadondientu.gdt.gov.vn', { waitUntil: 'networkidle2', timeout: 30000 });

    const result = await page.evaluate(async (tok, ep, search) => {
      let allInvoices = [];
      let state = null;
      let hasMore = true;
      let pageCount = 0;

      while (hasMore && pageCount < 50) {
        pageCount++;
        let url = `${ep}?sort=tdlap:desc&size=50&search=${search}`;
        if (state) {
          url += `&state=${encodeURIComponent(state)}`;
        }

        const res = await fetch(url, {
          headers: {
            'Authorization': `Bearer ${tok}`,
            'request-id': crypto.randomUUID(),
            'End-Point': '/tra-cuu/tra-cuu-hoa-don',
            'Action': ''
          }
        });

        if (!res.ok) {
          break;
        }

        const json = await res.json();
        const items = json.datas || json.data || (Array.isArray(json) ? json : []);
        if (items && items.length > 0) {
          allInvoices.push(...items);
        }

        if (json.state && json.state !== state && items.length > 0) {
          state = json.state;
        } else {
          hasMore = false;
        }
      }

      return { success: true, datas: allInvoices };
    }, token, endpoint, searchParam);

    console.log(JSON.stringify(result));
  } finally {
    await browser.close();
  }
}

(async () => {
  const args = process.argv.slice(2);
  const cmd = args[0];

  try {
    if (cmd === 'get-captcha') {
      await getCaptcha();
    } else if (cmd === 'login') {
      const uIdx = args.indexOf('--username');
      const pIdx = args.indexOf('--password');
      const cIdx = args.indexOf('--captcha');
      const kIdx = args.indexOf('--key');

      const u = uIdx !== -1 ? args[uIdx + 1] : '';
      const p = pIdx !== -1 ? args[pIdx + 1] : '';
      const c = cIdx !== -1 ? args[cIdx + 1] : '';
      const k = kIdx !== -1 ? args[kIdx + 1] : '';

      await login(u, p, c, k);
    } else if (cmd === 'fetch-invoices') {
      const tIdx = args.indexOf('--token');
      const eIdx = args.indexOf('--endpoint');
      const sIdx = args.indexOf('--search');

      const tok = tIdx !== -1 ? args[tIdx + 1] : '';
      const ep = eIdx !== -1 ? args[eIdx + 1] : '/api/query/invoices/purchase';
      const search = sIdx !== -1 ? args[sIdx + 1] : '';

      await fetchInvoices(tok, ep, search);
    } else {
      console.log(JSON.stringify({ success: false, message: 'Unknown command' }));
    }
  } catch (err) {
    console.log(JSON.stringify({ success: false, message: err.message }));
  }
})();
