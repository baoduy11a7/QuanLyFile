import sys, os, json, re, time, urllib.request, urllib.error
import resvg_py
import ddddocr

sys.stdout.reconfigure(encoding='utf-8')
_ocr = None

def get_ocr():
    global _ocr
    if _ocr is None:
        _ocr = ddddocr.DdddOcr(show_ad=False)
    return _ocr

def solve_svg_captcha(svg_content):
    if not svg_content:
        return ""
    if svg_content.strip().startswith('{'):
        try:
            d = json.loads(svg_content)
            svg_content = d.get('content', svg_content)
        except Exception:
            pass
    path_tags = re.findall(r'<path[^>]*>', svg_content)
    char_boxes = []
    for tag in path_tags:
        if 'fill="none"' in tag or 'stroke=' in tag:
            continue
        d_m = re.search(r'd="([^"]+)"', tag)
        if not d_m: continue
        d_val = d_m.group(1)
        coords = re.findall(r'([0-9]+\.?[0-9]*)\s+([0-9]+\.?[0-9]*)', d_val)
        if coords:
            xs = [float(c[0]) for c in coords]
            min_x, max_x = min(xs), max(xs)
            char_boxes.append({'min_x': min_x, 'max_x': max_x, 'tags': [tag]})

    char_boxes.sort(key=lambda b: b['min_x'])

    # Merge overlapping paths (dot over i, j, accents)
    merged = []
    for box in char_boxes:
        if not merged:
            merged.append(box)
        else:
            prev = merged[-1]
            if box['min_x'] <= prev['max_x'] - 2:
                prev['tags'].extend(box['tags'])
                prev['max_x'] = max(prev['max_x'], box['max_x'])
            else:
                merged.append(box)

    spaced_paths = []
    for i, group in enumerate(merged):
        target_x = 20 + i * 50
        shift_x = target_x - group['min_x']
        for tag in group['tags']:
            transformed_tag = re.sub(r'<path', f'<path transform="translate({shift_x:.1f}, 0)"', tag)
            spaced_paths.append(transformed_tag)

    total_w = max(200, 40 + len(merged) * 50)
    spaced_svg = f'<svg xmlns="http://www.w3.org/2000/svg" width="{total_w}" height="50" viewBox="0 0 {total_w} 50">{"".join(spaced_paths)}</svg>'

    png_bytes = resvg_py.svg_to_bytes(spaced_svg)
    ocr = get_ocr()
    text = ocr.classification(png_bytes).strip()
    return text

def fetch_captcha_http():
    headers = {
        'User-Agent': 'Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36',
        'Accept': 'application/json, text/plain, */*'
    }
    # Use gdt_agent.js if direct http is blocked by F5 WAF, or try direct first
    import subprocess
    node_script = os.path.join(os.path.dirname(__file__), 'gdt_agent.js')
    p = subprocess.run(['node', node_script, 'get-captcha'], capture_output=True, text=True)
    try:
        return json.loads(p.stdout.strip())
    except Exception as e:
        return {'success': False, 'message': str(e)}

def auto_login(username, password, max_retries=10):
    node_script = os.path.join(os.path.dirname(__file__), 'gdt_agent.js')
    import subprocess

    for attempt in range(max_retries):
        cap_data = fetch_captcha_http()
        if not cap_data.get('success'):
            time.sleep(0.5)
            continue

        key = cap_data.get('key')
        svg = cap_data.get('content')
        code = solve_svg_captcha(svg)
        if not code or len(code) < 4:
            continue

        cmd = [
            'node', node_script, 'login',
            '--username', username,
            '--password', password,
            '--captcha', code,
            '--key', key
        ]
        p_login = subprocess.run(cmd, capture_output=True, text=True, encoding='utf-8')
        try:
            res = json.loads(p_login.stdout.strip())
        except Exception:
            continue

        if res.get('success') and res.get('token'):
            return {
                'success': True,
                'token': res['token'],
                'attempt': attempt + 1
            }

        msg = res.get('message', '')
        msg_l = msg.lower()
        is_captcha_fail = ('captcha' in msg_l or 'xác thực' in msg_l or 'tham số không hợp lệ' in msg_l 
                           or 'hết hạn' in msg_l or 'không chính xác' in msg_l or '400' in msg_l)
        if is_captcha_fail:
            time.sleep(0.4)
            continue
        
        # If error is wrong password or locked account, return immediately
        return {
            'success': False,
            'message': msg,
            'attempt': attempt + 1
        }

    return {
        'success': False,
        'message': f'Đã thử tự động giải Captcha {max_retries} lần nhưng chưa thành công.'
    }

if __name__ == '__main__':
    if len(sys.argv) < 2:
        print(json.dumps({'success': False, 'message': 'Chưa truyền tham số.'}))
        sys.exit(1)

    cmd = sys.argv[1]
    if cmd == 'solve-svg':
        # SVG passed via stdin or file
        if len(sys.argv) > 2 and os.path.exists(sys.argv[2]):
            with open(sys.argv[2], 'r', encoding='utf-8-sig') as f:
                content = f.read()
        else:
            content = sys.stdin.read()
        code = solve_svg_captcha(content)
        print(json.dumps({'success': True, 'code': code}))
    elif cmd == 'auto-login':
        import argparse
        parser = argparse.ArgumentParser()
        parser.add_argument('cmd')
        parser.add_argument('--username', required=True)
        parser.add_argument('--password', required=True)
        args = parser.parse_args()
        result = auto_login(args.username, args.password)
        print(json.dumps(result, ensure_ascii=False))
