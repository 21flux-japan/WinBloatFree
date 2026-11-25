from playwright.sync_api import sync_playwright
import pathlib
d=pathlib.Path(__file__).parent; f=(d/'main.html').as_uri()
shots={'main-dark':'?theme=dark','main-light':'?theme=light','review':'?theme=dark&view=review','updates':'?theme=dark&view=updates'}
with sync_playwright() as p:
    b=p.chromium.launch(executable_path='/usr/bin/google-chrome')
    pg=b.new_page(viewport={'width':1340,'height':860},device_scale_factor=2)
    for n,qs in shots.items():
        pg.goto(f+qs); pg.wait_for_timeout(300)
        pg.screenshot(path=str(d/f'{n}.png'))
    b.close()
