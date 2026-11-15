import sys
from playwright.sync_api import sync_playwright
import oracle_chromium as O, arabic_ref as R
from PIL import Image
strings = sys.argv[1:]
opts = R.DisplayOptions()
with sync_playwright() as pw:
    b = pw.chromium.launch()
    nat = O.render_rows(b, [O.natural_frag(R.from_cps(R._isolate_text(s, opts))) for s in strings])
    vis = O.render_rows(b, [O.visual_frag(R.process_display(s, opts)) for s in strings])
    b.close()
imgs = []
for s, a, v in zip(strings, nat, vis):
    print(repr(s), '->', [hex(ord(c)) for c in R.process_display(s, opts)], O.compare_images(a, v), a.size, v.size)
    imgs += [a, v]
H = sum(i.size[1] for i in imgs) + 14*len(imgs); W = max(i.size[0] for i in imgs) + 20
sheet = Image.new('L', (W, H), 255); y = 7
for i in imgs: sheet.paste(i, (10, y)); y += i.size[1] + 14
sheet = sheet.resize((sheet.size[0]*4, sheet.size[1]*4), Image.NEAREST)
sheet.save('out/dbg.png')
