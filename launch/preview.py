"""Wraps GitHub's own rendering of README.md (from `gh api markdown`) in GitHub-like styling, with
every image inlined, so the README can be reviewed as a page before the repo is public."""
import base64
import os
import re
import sys
import urllib.request

HERE = os.path.dirname(os.path.abspath(__file__))
body = open(os.path.join(HERE, '_rendered.html'), encoding='utf-8').read()


def data_uri(path):
    kind = 'image/png' if path.endswith('.png') else 'image/svg+xml'
    with open(os.path.join(HERE, path), 'rb') as f:
        return f'data:{kind};base64,' + base64.b64encode(f.read()).decode()


def badge(url):
    url = url.replace('&amp;', '&')
    try:
        with urllib.request.urlopen(urllib.request.Request(url, headers={'User-Agent': 'Mozilla/5.0'}), timeout=15) as r:
            return 'data:image/svg+xml;base64,' + base64.b64encode(r.read()).decode()
    except Exception:
        return ''


# Local pictures, in src and srcset.
body = re.sub(r'(src|srcset)="(assets/[^"]+)"', lambda m: f'{m.group(1)}="{data_uri(m.group(2))}"', body)
# Badges: GitHub proxies them through camo; fetch the originals.
body = re.sub(r'src="https://camo[^"]+" data-canonical-src="([^"]+)"', lambda m: f'src="{badge(m.group(1))}"', body)
# The diagram: GitHub renders it in an iframe; the artifact viewer renders mermaid natively.
body = re.sub(r'<section class="js-render-needs-enrichment.*?<pre lang="mermaid"[^>]*>(.*?)</pre>.*?</section>',
              lambda m: '<pre class="mermaid">' + m.group(1) + '</pre>', body, flags=re.S)

page = '''<meta charset="utf-8"><title>Deskweave README Preview</title>
<style>
:root{--bg:#ffffff;--fg:#1f2328;--muted:#59636e;--line:#d1d9e0;--code:#f6f8fa;--link:#0969da;--chrome:#f6f8fa}
@media (prefers-color-scheme: dark){:root:not([data-theme="light"]){color-scheme:dark;--bg:#0d1117;--fg:#e6edf3;--muted:#9198a1;--line:#3d444d;--code:#151b23;--link:#4493f8;--chrome:#010409}}
:root[data-theme="dark"]{color-scheme:dark;--bg:#0d1117;--fg:#e6edf3;--muted:#9198a1;--line:#3d444d;--code:#151b23;--link:#4493f8;--chrome:#010409}
body{background:var(--chrome);color:var(--fg);font:16px/1.5 -apple-system,BlinkMacSystemFont,"Segoe UI","Noto Sans",Helvetica,Arial,sans-serif}
.repo{max-width:1012px;margin:0 auto;padding:24px 16px 60px}
.head{display:flex;align-items:center;gap:8px;font-size:20px;margin-bottom:16px;flex-wrap:wrap}
.head a{color:var(--link);text-decoration:none}.head b{font-weight:600}
.pill{font-size:12px;border:1px solid var(--line);border-radius:2em;padding:0 7px;color:var(--muted)}
.box{background:var(--bg);border:1px solid var(--line);border-radius:6px}
.box-h{padding:8px 16px;border-bottom:1px solid var(--line);font-size:14px;font-weight:600}
.md{padding:32px;overflow-wrap:break-word}
@media (max-width:600px){.md{padding:16px}}
.md a{color:var(--link);text-decoration:none}
.md h2{font-size:1.5em;font-weight:600;border-bottom:1px solid var(--line);padding-bottom:.3em;margin:24px 0 16px}
.md p,.md ul,.md ol,.md table,.md pre{margin:0 0 16px}
.md img{max-width:100%;box-sizing:content-box}
.md code{background:var(--code);border-radius:6px;padding:.2em .4em;font:85% ui-monospace,SFMono-Regular,Consolas,monospace}
.md pre{background:var(--code);border-radius:6px;padding:16px;overflow:auto;font:85%/1.45 ui-monospace,Consolas,monospace}
.md pre code{background:none;padding:0;font-size:100%}
.md table{border-collapse:collapse;display:block;overflow:auto;width:max-content;max-width:100%}
.md td,.md th{border:1px solid var(--line);padding:6px 13px}
.md kbd{display:inline-block;padding:3px 5px;font:11px ui-monospace,Consolas,monospace;border:1px solid var(--line);border-bottom-width:2px;border-radius:6px;background:var(--code)}
.md .anchor,.md .octicon{display:none}
.md .markdown-heading{position:relative}
.md div.highlight{margin-bottom:16px}
.note{max-width:1012px;margin:0 auto 12px;padding:0 16px;color:var(--muted);font-size:13px}
</style>
<p class="note">Preview only: GitHub's own renderer, not yet on GitHub. Switch your system or viewer theme to see the dark banner and screenshots.</p>
<div class="repo">
  <div class="head"><span>&#128193;</span><a href="#">JeffLepp</a><span>/</span><b><a href="#">Deskweave</a></b><span class="pill">Public</span></div>
  <div class="box"><div class="box-h">README</div><article class="md">''' + body + '''</article></div>
</div>
'''
out = sys.argv[1] if len(sys.argv) > 1 else os.path.join(HERE, 'readme-preview.html')
open(out, 'w', encoding='utf-8').write(page)
print(out, len(page))
