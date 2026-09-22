"""Renders the launch art: the app icon (PNG sizes and .ico) and any HTML page in this folder
to a PNG, through headless Chrome, so fonts and SVG render the way a browser does."""
import os
import subprocess
import sys
import tempfile
from PIL import Image

HERE = os.path.dirname(os.path.abspath(__file__))
ASSETS = os.path.join(HERE, 'assets')
CHROME = r'C:\Program Files\Google\Chrome\Application\chrome.exe'
PROFILE = os.path.join(tempfile.gettempdir(), 'dw-launch-chrome')


def shoot(url, out, width, height, scale=1):
    subprocess.run([CHROME, '--headless=new', '--disable-gpu', '--hide-scrollbars', '--no-first-run',
                    f'--force-device-scale-factor={scale}', '--default-background-color=00000000',
                    f'--user-data-dir={PROFILE}', f'--window-size={width},{height}',
                    '--virtual-time-budget=3000', f'--screenshot={out}', url],
                   check=True, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)


def file_url(path):
    return 'file:///' + path.replace('\\', '/')


def icon():
    page = os.path.join(ASSETS, '_icon.html')
    with open(page, 'w', encoding='utf-8') as f:
        f.write('<body style="margin:0;background:transparent"><img src="icon.svg" width="1024" height="1024"></body>')
    big = os.path.join(ASSETS, 'icon-1024.png')
    shoot(file_url(page), big, 1024, 1024)
    os.remove(page)
    master = Image.open(big).convert('RGBA')
    sizes = [16, 20, 24, 32, 40, 48, 64, 128, 256, 512]
    for s in sizes:
        master.resize((s, s), Image.LANCZOS).save(os.path.join(ASSETS, f'icon-{s}.png'))
    master.resize((256, 256), Image.LANCZOS).save(os.path.join(ASSETS, 'Deskweave.ico'),
                                                 sizes=[(s, s) for s in [16, 20, 24, 32, 40, 48, 64, 128, 256]])


def page(name, width, height, scale=2):
    """name may carry a query, "banner?dark", which lands in banner-dark.png."""
    base, _, query = name.partition('?')
    src = os.path.join(HERE, base + '.html')
    out = base + ('-' + query if query else '') + '.png'
    shoot(file_url(src) + ('?' + query if query else ''), os.path.join(ASSETS, out), width, height, scale)


if __name__ == '__main__':
    what = sys.argv[1:] or ['icon']
    for w in what:
        if w == 'icon':
            icon()
        else:
            n, width, height = w.split(':')
            page(n, int(width), int(height))
    print('ok')
