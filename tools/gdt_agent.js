const fs = require('fs');
const path = require('path');
const puppeteer = require('puppeteer-core');

function getBrowserPath() {
  const custom = process.env.CHROME_PATH;
  if (custom && fs.existsSync(custom)) return custom;

  const candidates = [
    '/usr/bin/chromium',
    '/usr/bin/chromium-browser',
    '/usr/bin/google-chrome',
    '/usr/bin/google-chrome-stable',
    'C:\\Program Files\\Google\\Chrome\\Application\\chrome.exe',
    'C:\\Program Files (x86)\\Google\\Chrome\\Application\\chrome.exe',
    'C:\\Program Files (x86)\\Microsoft\\Edge\\Application\\msedge.exe',
    'C:\\Program Files\\Microsoft\\Edge\\Application\\msedge.exe'
  ];

  for (const c of candidates) {
    if (fs.existsSync(c)) return c;
  }
  throw new Error('Không tìm thấy trình duyệt Google Chrome hoặc Chromium trên hệ thống.');
}

async function createBrowserAndPage() {
  const browserPath = getBrowserPath();
  const browser = await puppeteer.launch({
    executablePath: browserPath,
    headless: 'new',
    args: [
      '--no-sandbox',
      '--disable-setuid-sandbox',
      '--disable-blink-features=AutomationControlled',
      '--disable-dev-shm-usage',
      '--disable-gpu'
    ]
  });

  const page = await browser.newPage();

  // Thiết lập User-Agent chuẩn desktop để tránh bị tường lửa chặn
  await page.setUserAgent('Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36');
  await page.setViewport({ width: 1280, height: 800 });

  // Chặn nạp các tài nguyên nặng (hình ảnh, css, fonts, media và các bundle chunk Next.js)
  // Máy chủ Cổng Thuế hoadondientu.gdt.gov.vn thường nghẽn khi tải hàng chục file chunk .js,
  // dẫn tới lỗi Navigation timeout (networkidle2/load).
  // Vì ta chỉ cần ngữ cảnh origin + cookie để gọi API fetch(), việc bỏ qua các file này giúp tải trang trong ~300ms.
  await page.setRequestInterception(true);
  page.on('request', (req) => {
    try {
      const rt = req.resourceType();
      const url = req.url();
      if (['image', 'stylesheet', 'font', 'media'].includes(rt) || url.includes('/_next/')) {
        req.abort();
      } else {
        req.continue();
      }
    } catch (_) {
      try { req.continue(); } catch (_) {}
    }
  });

  // Điều hướng với domcontentloaded và thời gian timeout 60s
  await page.goto('https://hoadondientu.gdt.gov.vn', {
    waitUntil: 'domcontentloaded',
    timeout: 60000
  });

  return { browser, page };
}

async function getCaptcha() {
  const { browser, page } = await createBrowserAndPage();

  try {
    const result = await page.evaluate(async () => {
      const controller = new AbortController();
      const tid = setTimeout(() => controller.abort(), 20000);

      try {
        const res = await fetch('/api/captcha', { signal: controller.signal });
        clearTimeout(tid);

        if (!res.ok) {
          return { success: false, message: `Lỗi HTTP ${res.status} khi lấy mã captcha từ Cổng Thuế` };
        }
        const data = await res.json();
        return { success: true, key: data.key, content: data.content };
      } catch (err) {
        clearTimeout(tid);
        return { success: false, message: `Lỗi kết nối khi lấy captcha: ${err.message}` };
      }
    });

    console.log(JSON.stringify(result));
  } finally {
    await browser.close();
  }
}

async function login(username, password, captcha, key) {
  const { browser, page } = await createBrowserAndPage();

  try {
    const result = await page.evaluate(async (u, p, c, k) => {
      const controller = new AbortController();
      const tid = setTimeout(() => controller.abort(), 25000);

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
          }),
          signal: controller.signal
        });
        clearTimeout(tid);

        const text = await res.text();
        let json = null;
        try { json = JSON.parse(text); } catch (_) {}

        if (res.ok) {
          const token = json?.token || json?.access_token || json?.jwt;
          if (token) {
            return { success: true, token };
          }
          return { success: false, message: 'Đăng nhập thành công nhưng không nhận được Token từ Cổng Thuế' };
        }

        const msg = json?.message || (text.includes('Request Rejected') ? 'Yêu cầu bị Tường lửa (F5 WAF) của Cổng Thuế chặn' : text) || `Lỗi HTTP ${res.status}`;
        return { success: false, status: res.status, message: msg };
      } catch (err) {
        clearTimeout(tid);
        return { success: false, message: `Lỗi khi xác thực: ${err.message}` };
      }
    }, username, password, captcha, key);

    console.log(JSON.stringify(result));
  } finally {
    await browser.close();
  }
}

async function fetchInvoices(token, endpoint, searchParam) {
  const { browser, page } = await createBrowserAndPage();

  try {
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

        const controller = new AbortController();
        const tid = setTimeout(() => controller.abort(), 30000);

        try {
          const res = await fetch(url, {
            headers: {
              'Authorization': `Bearer ${tok}`,
              'request-id': crypto.randomUUID(),
              'End-Point': '/tra-cuu/tra-cuu-hoa-don',
              'Action': ''
            },
            signal: controller.signal
          });
          clearTimeout(tid);

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
        } catch (_) {
          clearTimeout(tid);
          break;
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
      console.log(JSON.stringify({ success: false, message: 'Lệnh không hợp lệ' }));
    }
  } catch (err) {
    let msg = err.message || 'Lỗi không xác định khi giao tiếp với trình duyệt';
    if (msg.includes('Navigation timeout')) {
      msg = 'Cổng Thuế phản hồi quá lâu (Navigation Timeout). Vui lòng thử lại sau ít phút.';
    }
    console.log(JSON.stringify({ success: false, message: msg }));
  }
})();
