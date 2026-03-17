import base64

def get_svg(color1, color2, animate=False):
    anim = '<animate attributeName="opacity" values="0.5;1;0.5" dur="2s" repeatCount="indefinite" />' if animate else ''
    s = f'<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 100 100" width="11" height="11"><defs><linearGradient id="m" x1="0%" y1="0%" x2="100%" y2="100%"><stop offset="0%" stop-color="{color1}" /><stop offset="100%" stop-color="{color2}" /></linearGradient></defs><circle cx="50" cy="50" r="48" fill="url(#m)">{anim}</circle><ellipse cx="50" cy="25" rx="20" ry="10" fill="rgba(255,255,255,0.2)"/></svg>'
    return "data:image/svg+xml;base64," + base64.b64encode(s.encode('utf-8')).decode('utf-8')

print("slateSvg = " + get_svg("#94a3b8", "#475569"))
print("amberAnimSvg = " + get_svg("#fbbf24", "#d97706", True))
print("mintSvg = " + get_svg("#34d399", "#059669"))
print("brickSvg = " + get_svg("#f87171", "#b91c1c"))
