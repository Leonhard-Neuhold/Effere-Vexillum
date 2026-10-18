// Flag drawing editor engine. The drawing is a small scene of objects (strokes, shapes, flood fills)
// rendered onto a base canvas; a second canvas on top carries grid, previews and selection handles.
// DrawingCanvas.razor owns the toolbar and talks to this module through the object returned by create().

const LONG_SIDE = 1200;
const MAX_ZOOM = 8;
const HISTORY_LIMIT = 100;
const HANDLE_PX = 10;
const HIT_PX = 12;
const ERASER_SCALE = 2;
const DEFAULT_RATIO = { w: 3, h: 2 };

const SHAPE_TYPES = ['rect', 'ellipse', 'triangle', 'hoist', 'star', 'crescent', 'cross', 'sun'];
const HOTKEYS = {
    v: 'select', h: 'pan', b: 'pen', e: 'eraser', g: 'fill', i: 'pick', l: 'line', r: 'rect', o: 'ellipse',
    t: 'triangle', j: 'hoist', y: 'polygon', s: 'star', c: 'crescent', x: 'cross', u: 'sun'
};

let nextId = 1;
let nextEditorId = 1;
const editors = new Map();

const clamp = (v, lo, hi) => Math.min(hi, Math.max(lo, v));
const dist = (a, b) => Math.hypot(a.x - b.x, a.y - b.y);

function rotate(p, c, a) {
    const cos = Math.cos(a), sin = Math.sin(a), dx = p.x - c.x, dy = p.y - c.y;
    return { x: c.x + dx * cos - dy * sin, y: c.y + dx * sin + dy * cos };
}

function boxOf(pts) {
    let x0 = Infinity, y0 = Infinity, x1 = -Infinity, y1 = -Infinity;
    for (const p of pts) {
        x0 = Math.min(x0, p.x); y0 = Math.min(y0, p.y);
        x1 = Math.max(x1, p.x); y1 = Math.max(y1, p.y);
    }
    return { x0, y0, x1, y1, w: x1 - x0, h: y1 - y0 };
}

function centerOf(o) {
    const b = boxOf(o.pts);
    return { x: (b.x0 + b.x1) / 2, y: (b.y0 + b.y1) / 2 };
}

function cloneObj(o) {
    return { ...o, pts: o.pts ? o.pts.map(p => ({ x: p.x, y: p.y })) : o.pts };
}

function hexToRgb(hex) {
    const n = parseInt(hex.slice(1), 16);
    return [(n >> 16) & 255, (n >> 8) & 255, n & 255];
}

function rgbToHex(r, g, b) {
    return '#' + [r, g, b].map(v => v.toString(16).padStart(2, '0')).join('');
}

// ---- unit shapes -----------------------------------------------------------------------------
// Shapes other than rect/ellipse are defined in a unit space and fitted into the object's bounding
// box, so dragging a box always gives a shape that touches all four sides of it.

const unitCache = new Map();

function fromVerts(verts) {
    const path = new Path2D();
    verts.forEach((v, i) => i ? path.lineTo(v[0], v[1]) : path.moveTo(v[0], v[1]));
    path.closePath();
    const xs = verts.map(v => v[0]), ys = verts.map(v => v[1]);
    return { path, x0: Math.min(...xs), y0: Math.min(...ys), x1: Math.max(...xs), y1: Math.max(...ys) };
}

function starVerts(n) {
    // Inner radius of the regular {n/2} star polygon; plain 0.5 where that is degenerate.
    const inner = n >= 5 ? Math.cos(2 * Math.PI / n) / Math.cos(Math.PI / n) : 0.5;
    const verts = [];
    for (let i = 0; i < n * 2; i++) {
        const r = i % 2 ? inner : 1;
        const a = -Math.PI / 2 + i * Math.PI / n;
        verts.push([r * Math.cos(a), r * Math.sin(a)]);
    }
    return verts;
}

function crossVerts(t) {
    const a = (1 - t) / 2, b = (1 + t) / 2;
    return [[a, 0], [b, 0], [b, a], [1, a], [1, b], [b, b], [b, 1], [a, 1], [a, b], [0, b], [0, a], [a, a]];
}

function crescentShape() {
    // Outer unit circle minus a smaller circle shifted right; the horns point right.
    const d = 0.35, r2 = 0.8;
    const x = (d * d + 1 - r2 * r2) / (2 * d), y = Math.sqrt(1 - x * x);
    const theta = Math.atan2(y, x), phi = Math.atan2(y, x - d);
    const path = new Path2D();
    path.arc(0, 0, 1, theta, 2 * Math.PI - theta, false);
    path.arc(d, 0, r2, 2 * Math.PI - phi, phi, true);
    path.closePath();
    return { path, x0: -1, y0: -1, x1: x, y1: 1 };
}

function sunShape(rays) {
    const path = new Path2D();
    path.arc(0, 0, 0.55, 0, 2 * Math.PI);
    path.closePath();
    for (let i = 0; i < rays; i++) {
        const a = -Math.PI / 2 + i * 2 * Math.PI / rays, half = Math.PI / rays * 0.55;
        path.moveTo(0.5 * Math.cos(a - half), 0.5 * Math.sin(a - half));
        path.lineTo(Math.cos(a), Math.sin(a));
        path.lineTo(0.5 * Math.cos(a + half), 0.5 * Math.sin(a + half));
        path.closePath();
    }
    return { path, x0: -1, y0: -1, x1: 1, y1: 1 };
}

function unitShape(o) {
    const key = `${o.type}:${o.n}:${o.t}`;
    let u = unitCache.get(key);
    if (u) return u;
    switch (o.type) {
        case 'triangle': u = fromVerts([[0.5, 0], [1, 1], [0, 1]]); break;
        case 'hoist': u = fromVerts([[0, 0], [1, 0.5], [0, 1]]); break;
        case 'star': u = fromVerts(starVerts(o.n)); break;
        case 'cross': u = fromVerts(crossVerts(o.t / 100)); break;
        case 'crescent': u = crescentShape(); break;
        case 'sun': u = sunShape(o.n); break;
    }
    unitCache.set(key, u);
    return u;
}

// Width / height of the shape when drawn "regular" (used by the constrain toggle).
function naturalAspect(o) {
    switch (o.type) {
        case 'triangle': return 2 / Math.sqrt(3);
        case 'hoist': return Math.sqrt(3) / 2;
        case 'star':
        case 'crescent': {
            const u = unitShape(o);
            return (u.x1 - u.x0) / (u.y1 - u.y0);
        }
        default: return 1;
    }
}

function pathFor(o) {
    const pts = o.pts;
    const path = new Path2D();
    switch (o.type) {
        case 'pen':
            if (pts.length === 1) {
                path.arc(pts[0].x, pts[0].y, o.width / 2, 0, 2 * Math.PI);
                break;
            }
            path.moveTo(pts[0].x, pts[0].y);
            for (let i = 1; i < pts.length - 1; i++) {
                path.quadraticCurveTo(pts[i].x, pts[i].y, (pts[i].x + pts[i + 1].x) / 2, (pts[i].y + pts[i + 1].y) / 2);
            }
            path.lineTo(pts[pts.length - 1].x, pts[pts.length - 1].y);
            break;
        case 'line':
            path.moveTo(pts[0].x, pts[0].y);
            path.lineTo(pts[1].x, pts[1].y);
            break;
        case 'polygon':
            pts.forEach((p, i) => i ? path.lineTo(p.x, p.y) : path.moveTo(p.x, p.y));
            path.closePath();
            break;
        case 'rect': {
            const b = boxOf(pts);
            path.rect(b.x0, b.y0, b.w, b.h);
            break;
        }
        case 'ellipse': {
            const b = boxOf(pts);
            path.ellipse((b.x0 + b.x1) / 2, (b.y0 + b.y1) / 2, b.w / 2, b.h / 2, 0, 0, 2 * Math.PI);
            break;
        }
        default: {
            const u = unitShape(o), b = boxOf(pts);
            const flipX = pts[1].x < pts[0].x, flipY = pts[1].y < pts[0].y;
            const a = (flipX ? -1 : 1) * Math.max(b.w, 0.001) / (u.x1 - u.x0);
            const d = (flipY ? -1 : 1) * Math.max(b.h, 0.001) / (u.y1 - u.y0);
            const e = (flipX ? b.x1 : b.x0) - a * u.x0;
            const f = (flipY ? b.y1 : b.y0) - d * u.y0;
            path.addPath(u.path, new DOMMatrix([a, 0, 0, d, e, f]));
        }
    }
    return path;
}

// Strokes (and unfilled outlines) are painted with the pen; everything else is filled.
const isStroked = o => o.type === 'line' || (o.type === 'pen' && o.pts.length > 1) || (o.type !== 'pen' && !o.filled);

function drawObj(ctx, o) {
    if (o.type === 'fill') {
        ctx.drawImage(o.bmp, o.x, o.y, o.w, o.h);
        return;
    }
    ctx.save();
    if (o.rot) {
        const c = centerOf(o);
        ctx.translate(c.x, c.y);
        ctx.rotate(o.rot);
        ctx.translate(-c.x, -c.y);
    }
    ctx.fillStyle = ctx.strokeStyle = o.color;
    ctx.lineWidth = o.width;
    ctx.lineJoin = o.type === 'pen' ? 'round' : 'miter';
    ctx.lineCap = o.type === 'pen' ? 'round' : 'butt';
    const path = pathFor(o);
    if (isStroked(o)) ctx.stroke(path); else ctx.fill(path);
    ctx.restore();
}

// ---- flood fill ------------------------------------------------------------------------------

function floodFill(ctx, W, H, sx, sy, color, tolerance) {
    const data = ctx.getImageData(0, 0, W, H).data;
    const i0 = (sy * W + sx) * 4;
    const tr = data[i0], tg = data[i0 + 1], tb = data[i0 + 2];
    const diff = i => Math.abs(data[i * 4] - tr) + Math.abs(data[i * 4 + 1] - tg) + Math.abs(data[i * 4 + 2] - tb);
    const mask = new Uint8Array(W * H);
    const stack = [sx, sy];
    while (stack.length) {
        const cy = stack.pop(), cx = stack.pop();
        if (mask[cy * W + cx] || diff(cy * W + cx) > tolerance) continue;
        let lx = cx, rx = cx;
        while (lx > 0 && !mask[cy * W + lx - 1] && diff(cy * W + lx - 1) <= tolerance) lx--;
        while (rx < W - 1 && !mask[cy * W + rx + 1] && diff(cy * W + rx + 1) <= tolerance) rx++;
        for (let x = lx; x <= rx; x++) mask[cy * W + x] = 1;
        for (const ny of [cy - 1, cy + 1]) {
            if (ny < 0 || ny >= H) continue;
            let inRun = false;
            for (let x = lx; x <= rx; x++) {
                const ok = !mask[ny * W + x] && diff(ny * W + x) <= tolerance;
                if (ok && !inRun) stack.push(x, ny);
                inRun = ok;
            }
        }
    }

    // Grow the region by one pixel into anti-aliased edge pixels that are still mostly the old colour,
    // so no pale halo is left next to outlines. Hard boundaries to other flat colours stay untouched.
    const grown = mask.slice();
    const edgeTolerance = tolerance * 3.5;
    let count = 0;
    for (let y = 0; y < H; y++) {
        for (let x = 0; x < W; x++) {
            const i = y * W + x;
            if (mask[i]) { count++; continue; }
            const near = (x > 0 && mask[i - 1]) || (x < W - 1 && mask[i + 1]) || (y > 0 && mask[i - W]) || (y < H - 1 && mask[i + W]);
            if (near && diff(i) <= edgeTolerance) grown[i] = 1;
        }
    }
    if (!count) return null;

    const [r, g, b] = hexToRgb(color);
    const bmp = document.createElement('canvas');
    bmp.width = W;
    bmp.height = H;
    const bctx = bmp.getContext('2d');
    const out = bctx.createImageData(W, H);
    for (let i = 0; i < grown.length; i++) {
        if (!grown[i]) continue;
        out.data[i * 4] = r;
        out.data[i * 4 + 1] = g;
        out.data[i * 4 + 2] = b;
        out.data[i * 4 + 3] = 255;
    }
    bctx.putImageData(out, 0, 0);
    return bmp;
}

// ---- editor ----------------------------------------------------------------------------------

export function create(host, dotnet) {
    const S = {
        W: 0, H: 0,
        ratio: { ...DEFAULT_RATIO },
        objects: [],
        states: [],
        idx: -1,
        tool: 'pen',
        opts: { color: '#ce1126', width: 8, filled: true, n: 5, t: 33, tol: 60, grid: false, div: 6, snap: false, constrain: false },
        sel: null,
        drag: null,
        poly: null,
        hover: null,
        view: { z: 1, x: 0, y: 0 },
        pointers: new Map(),
        pinch: null,
        locked: false,
        space: false,
        dirty: false,
        notifyPending: false,
        lastSelId: null,
        forceSel: false,
        disposed: false
    };

    // DOM
    const wrap = document.createElement('div');
    const stack = document.createElement('div');
    const base = document.createElement('canvas');
    const over = document.createElement('canvas');
    wrap.style.cssText = 'position:relative;overflow:hidden;margin:0 auto;touch-action:none;user-select:none;-webkit-user-select:none;background:#fff;outline:1px solid rgba(0,0,0,.45);';
    stack.style.cssText = 'position:absolute;inset:0;transform-origin:0 0;';
    const canvasCss = 'position:absolute;inset:0;width:100%;height:100%;touch-action:none;';
    base.style.cssText = canvasCss;
    over.style.cssText = canvasCss;
    stack.append(base, over);
    wrap.append(stack);
    host.append(wrap);
    const bctx = base.getContext('2d', { willReadFrequently: true });
    const octx = over.getContext('2d');

    // ---- geometry helpers

    const logical = e => {
        const r = over.getBoundingClientRect();
        return { x: (e.clientX - r.left) * S.W / r.width, y: (e.clientY - r.top) * S.H / r.height };
    };
    // Logical pixels per screen pixel, so handles keep their on-screen size at any zoom.
    const pxScale = () => S.W / over.getBoundingClientRect().width;

    function snapPoint(p) {
        let { x, y } = p;
        if (S.opts.snap) {
            const stepX = S.W / S.opts.div, stepY = S.H / S.opts.div;
            x = Math.round(x / stepX) * stepX;
            y = Math.round(y / stepY) * stepY;
        }
        return { x: Math.round(clamp(x, 0, S.W)), y: Math.round(clamp(y, 0, S.H)) };
    }

    // ---- rendering

    function schedule() {
        if (S.dirty) return;
        S.dirty = true;
        requestAnimationFrame(() => {
            S.dirty = false;
            if (S.disposed) return;
            renderBase();
            renderOver();
            if (S.notifyPending) sendState();
        });
    }

    function renderBase() {
        bctx.fillStyle = '#fff';
        bctx.fillRect(0, 0, S.W, S.H);
        for (const o of S.objects) drawObj(bctx, o);
    }

    function handlesOf(o) {
        const b = boxOf(o.pts), k = pxScale();
        const cx = (b.x0 + b.x1) / 2, cy = (b.y0 + b.y1) / 2;
        const list = [];
        for (const [hy, y] of [[-1, b.y0], [0, cy], [1, b.y1]]) {
            for (const [hx, x] of [[-1, b.x0], [0, cx], [1, b.x1]]) {
                if (hx || hy) list.push({ hx, hy, x, y });
            }
        }
        list.push({ hx: 0, hy: 0, rot: true, x: cx, y: b.y0 - 28 * k });
        return { box: b, list };
    }

    function renderOver() {
        const k = pxScale();
        octx.clearRect(0, 0, S.W, S.H);

        if (S.drag && (S.drag.kind === 'shape' || S.drag.kind === 'stroke')) drawObj(octx, S.drag.obj);
        if (S.poly) drawPolygonPreview(k);
        if (S.opts.grid) drawGrid(k);
        if (S.tool === 'select' && S.sel) drawSelection(S.sel, k);
        drawCursorHint(k);
    }

    function drawGrid(k) {
        octx.save();
        octx.lineWidth = k;
        for (let i = 1; i < S.opts.div; i++) {
            const x = S.W * i / S.opts.div, y = S.H * i / S.opts.div;
            const mid = S.opts.div % 2 === 0 && i === S.opts.div / 2;
            octx.strokeStyle = mid ? 'rgba(25,118,210,.9)' : 'rgba(25,118,210,.5)';
            octx.beginPath();
            octx.moveTo(x, 0); octx.lineTo(x, S.H);
            octx.moveTo(0, y); octx.lineTo(S.W, y);
            octx.stroke();
        }
        octx.restore();
    }

    function drawSelection(o, k) {
        const { box, list } = handlesOf(o);
        const c = centerOf(o), size = HANDLE_PX * k;
        octx.save();
        octx.translate(c.x, c.y);
        octx.rotate(o.rot || 0);
        octx.translate(-c.x, -c.y);
        octx.lineWidth = 1.5 * k;
        octx.strokeStyle = '#1976d2';
        octx.setLineDash([6 * k, 4 * k]);
        octx.strokeRect(box.x0, box.y0, box.w, box.h);
        octx.setLineDash([]);
        const rot = list.find(h => h.rot);
        octx.beginPath();
        octx.moveTo((box.x0 + box.x1) / 2, box.y0);
        octx.lineTo(rot.x, rot.y);
        octx.stroke();
        octx.fillStyle = '#fff';
        for (const h of list) {
            octx.beginPath();
            if (h.rot) octx.arc(h.x, h.y, size / 2, 0, 2 * Math.PI);
            else octx.rect(h.x - size / 2, h.y - size / 2, size, size);
            octx.fill();
            octx.stroke();
        }
        octx.restore();
    }

    function drawPolygonPreview(k) {
        const pts = S.poly.pts, end = S.hover;
        octx.save();
        octx.lineWidth = Math.max(S.opts.width, k);
        octx.strokeStyle = S.opts.color;
        octx.lineJoin = 'round';
        if (S.opts.filled && pts.length >= 2) {
            octx.globalAlpha = 0.45;
            octx.fillStyle = S.opts.color;
            octx.beginPath();
            pts.forEach((p, i) => i ? octx.lineTo(p.x, p.y) : octx.moveTo(p.x, p.y));
            if (end) octx.lineTo(end.x, end.y);
            octx.closePath();
            octx.fill();
            octx.globalAlpha = 1;
        }
        octx.beginPath();
        pts.forEach((p, i) => i ? octx.lineTo(p.x, p.y) : octx.moveTo(p.x, p.y));
        if (end) octx.lineTo(end.x, end.y);
        octx.stroke();
        octx.lineWidth = 1.5 * k;
        octx.strokeStyle = '#1976d2';
        octx.fillStyle = '#fff';
        for (const p of pts) {
            octx.beginPath();
            octx.arc(p.x, p.y, 4 * k, 0, 2 * Math.PI);
            octx.fill();
            octx.stroke();
        }
        octx.restore();
    }

    // Brush ring for pen/eraser, and a snap marker for shape tools.
    function drawCursorHint(k) {
        if (!S.hover || S.drag || S.pinch) return;
        const h = S.hover;
        octx.save();
        octx.lineWidth = k;
        if (S.tool === 'pen' || S.tool === 'eraser') {
            const r = (S.tool === 'eraser' ? S.opts.width * ERASER_SCALE : S.opts.width) / 2;
            octx.beginPath();
            octx.arc(h.x, h.y, Math.max(r, k), 0, 2 * Math.PI);
            octx.strokeStyle = 'rgba(0,0,0,.7)';
            octx.stroke();
            octx.strokeStyle = 'rgba(255,255,255,.7)';
            octx.lineWidth = 0.5 * k;
            octx.stroke();
        } else if (S.opts.snap && S.tool !== 'select' && S.tool !== 'pan') {
            octx.strokeStyle = '#1976d2';
            octx.beginPath();
            octx.moveTo(h.x - 6 * k, h.y); octx.lineTo(h.x + 6 * k, h.y);
            octx.moveTo(h.x, h.y - 6 * k); octx.lineTo(h.x, h.y + 6 * k);
            octx.stroke();
        }
        octx.restore();
    }

    // ---- canvas size and view

    function applySize() {
        base.width = over.width = S.W;
        base.height = over.height = S.H;
        const r = S.W / S.H;
        wrap.style.aspectRatio = `${S.W} / ${S.H}`;
        wrap.style.width = `min(100%, calc(70vh * ${r}))`;
        clampView();
        applyView();
    }

    function clampView() {
        const v = S.view;
        v.x = clamp(v.x, wrap.clientWidth * (1 - v.z), 0);
        v.y = clamp(v.y, wrap.clientHeight * (1 - v.z), 0);
    }

    function applyView() {
        stack.style.transform = `translate(${S.view.x}px, ${S.view.y}px) scale(${S.view.z})`;
    }

    function zoomAt(sx, sy, factor) {
        const v = S.view, z = clamp(v.z * factor, 1, MAX_ZOOM);
        v.x = sx - (sx - v.x) * z / v.z;
        v.y = sy - (sy - v.y) * z / v.z;
        v.z = z;
        clampView();
        applyView();
        schedule();
        notify();
    }

    function zoomCenter(factor) {
        zoomAt(wrap.clientWidth / 2, wrap.clientHeight / 2, factor);
    }

    function setRatio(w, h, stretch) {
        w = clamp(+w || 1, 0.1, 100);
        h = clamp(+h || 1, 0.1, 100);
        const r = clamp(w / h, 0.2, 5);
        const W = Math.round(r >= 1 ? LONG_SIDE : LONG_SIDE * r);
        const H = Math.round(r >= 1 ? LONG_SIDE / r : LONG_SIDE);
        if (W === S.W && H === S.H) {
            S.ratio = { w, h };
            notify();
            return;
        }
        if (S.W && stretch) {
            const sx = W / S.W, sy = H / S.H;
            for (const o of S.objects) {
                if (o.type === 'fill') {
                    o.x *= sx; o.y *= sy; o.w *= sx; o.h *= sy;
                } else {
                    for (const p of o.pts) { p.x *= sx; p.y *= sy; }
                }
            }
        }
        S.W = W;
        S.H = H;
        S.ratio = { w, h };
        S.poly = null;
        applySize();
        commit();
    }

    // ---- history

    const snapshot = () => ({ W: S.W, H: S.H, ratio: { ...S.ratio }, objects: S.objects.map(cloneObj) });

    function commit() {
        S.states.length = S.idx + 1;
        S.states.push(snapshot());
        if (S.states.length > HISTORY_LIMIT) S.states.shift();
        S.idx = S.states.length - 1;
        schedule();
        notify();
    }

    function restore(i) {
        const st = S.states[i];
        S.idx = i;
        S.W = st.W;
        S.H = st.H;
        S.ratio = { ...st.ratio };
        S.objects = st.objects.map(cloneObj);
        S.sel = S.sel ? S.objects.find(o => o.id === S.sel.id) || null : null;
        S.forceSel = true;
        S.poly = null;
        applySize();
        schedule();
        notify();
    }

    function undo() {
        cancelPolygon();
        if (S.idx > 0) restore(S.idx - 1);
    }

    function redo() {
        if (S.idx < S.states.length - 1) restore(S.idx + 1);
    }

    // ---- notifications to Blazor

    function notify() {
        S.notifyPending = true;
        schedule();
    }

    function sendState() {
        S.notifyPending = false;
        const state = {
            canUndo: S.idx > 0,
            canRedo: S.idx < S.states.length - 1,
            selType: S.sel ? S.sel.type : '',
            polygonActive: !!S.poly,
            zoom: Math.round(S.view.z * 100),
            ratioW: S.ratio.w,
            ratioH: S.ratio.h,
            selection: null
        };
        const id = S.sel ? S.sel.id : null;
        if (S.sel && (id !== S.lastSelId || S.forceSel)) {
            const o = S.sel;
            state.selection = { color: o.color, width: o.width, filled: !!o.filled, n: o.n, t: o.t };
        }
        S.lastSelId = id;
        S.forceSel = false;
        dotnet.invokeMethodAsync('OnEditorState', state);
    }

    // ---- scene editing

    function addObject(o) {
        S.objects.push(o);
        commit();
    }

    function newShape(type, pts) {
        const { color, width, filled, n, t } = S.opts;
        return { id: nextId++, type, pts, color, width, filled, n, t, rot: 0 };
    }

    function select(o) {
        S.sel = o;
        notify();
        schedule();
    }

    function applyToSelection(name, value) {
        const o = S.sel;
        if (!o) return;
        if (name === 'color') o.color = value;
        else if (name === 'width' && o.type !== 'fill') o.width = value;
        else if (name === 'filled' && o.type !== 'pen' && o.type !== 'line') o.filled = value;
        else if (name === 'n' && (o.type === 'star' || o.type === 'sun')) o.n = value;
        else if (name === 't' && o.type === 'cross') o.t = value;
        else return;
        commit();
    }

    function setOption(name, value) {
        S.opts[name] = value;
        if (['color', 'width', 'filled', 'n', 't'].includes(name)) applyToSelection(name, value);
        schedule();
    }

    function setTool(tool) {
        if (tool !== 'polygon') finishPolygon();
        S.tool = tool;
        if (tool !== 'select') S.sel = null;
        updateCursor();
        notify();
        schedule();
    }

    function updateCursor() {
        const t = S.space ? 'pan' : S.tool;
        over.style.cursor = { select: 'default', pan: 'grab', fill: 'cell', pen: 'none', eraser: 'none' }[t] || 'crosshair';
    }

    function finishPolygon() {
        if (!S.poly) return;
        const pts = S.poly.pts;
        S.poly = null;
        if (pts.length >= 3) addObject(newShape('polygon', pts));
        else { notify(); schedule(); }
    }

    function cancelPolygon() {
        if (!S.poly) return;
        S.poly = null;
        notify();
        schedule();
    }

    function removeSelected() {
        if (!S.sel) return;
        S.objects = S.objects.filter(o => o !== S.sel);
        S.sel = null;
        commit();
    }

    function duplicateSelected() {
        if (!S.sel) return;
        const copy = cloneObj(S.sel);
        copy.id = nextId++;
        for (const p of copy.pts) { p.x += 20; p.y += 20; }
        S.objects.push(copy);
        S.sel = copy;
        commit();
    }

    function reorderSelected(toFront) {
        if (!S.sel) return;
        S.objects = S.objects.filter(o => o !== S.sel);
        if (toFront) S.objects.push(S.sel); else S.objects.unshift(S.sel);
        commit();
    }

    function flipSelected(horizontal) {
        const o = S.sel;
        if (!o) return;
        const c = centerOf(o);
        for (const p of o.pts) {
            if (horizontal) p.x = 2 * c.x - p.x; else p.y = 2 * c.y - p.y;
        }
        o.rot = -(o.rot || 0);
        commit();
    }

    function clearAll() {
        cancelPolygon();
        if (!S.objects.length) return;
        S.objects = [];
        S.sel = null;
        commit();
    }

    function nudge(dx, dy) {
        if (!S.sel) return;
        for (const p of S.sel.pts) { p.x += dx; p.y += dy; }
        commit();
    }

    // ---- hit testing

    function hitTest(o, p, k) {
        if (o.type === 'fill') return false;
        const q = o.rot ? rotate(p, centerOf(o), -o.rot) : p;
        const path = pathFor(o);
        octx.save();
        octx.setTransform(1, 0, 0, 1, 0, 0);
        octx.lineWidth = Math.max(o.width || 0, HIT_PX * k);
        const hit = isStroked(o)
            ? octx.isPointInStroke(path, q.x, q.y)
            : octx.isPointInPath(path, q.x, q.y) || octx.isPointInStroke(path, q.x, q.y);
        octx.restore();
        return hit;
    }

    function hitHandle(o, p, k, touch) {
        const c = centerOf(o), q = o.rot ? rotate(p, c, -o.rot) : p;
        const reach = (touch ? HANDLE_PX * 1.6 : HANDLE_PX) * k;
        for (const h of handlesOf(o).list) {
            if (Math.abs(q.x - h.x) <= reach && Math.abs(q.y - h.y) <= reach) return h;
        }
        return null;
    }

    // ---- pointer handling

    function constrainShape(type, a, b) {
        const dx = b.x - a.x, dy = b.y - a.y;
        if (type === 'line') {
            const step = Math.PI / 12, len = Math.hypot(dx, dy);
            const ang = Math.round(Math.atan2(dy, dx) / step) * step;
            return { x: Math.round(a.x + len * Math.cos(ang)), y: Math.round(a.y + len * Math.sin(ang)) };
        }
        const asp = naturalAspect({ type, n: S.opts.n, t: S.opts.t });
        const w = Math.max(Math.abs(dx), Math.abs(dy) * asp), h = w / asp;
        return { x: Math.round(a.x + Math.sign(dx || 1) * w), y: Math.round(a.y + Math.sign(dy || 1) * h) };
    }

    function effectiveTool(e) {
        if (e.button === 1 || S.space) return 'pan';
        return S.tool;
    }

    function onDown(e) {
        if (e.button === 2) return;
        over.setPointerCapture(e.pointerId);
        S.pointers.set(e.pointerId, { x: e.clientX, y: e.clientY });
        if (S.pointers.size >= 2) {
            beginPinch();
            return;
        }
        if (S.locked) return;

        const tool = effectiveTool(e);
        const raw = logical(e), k = pxScale();
        const touch = e.pointerType === 'touch';

        if (tool === 'pan') {
            S.drag = { kind: 'pan', x: e.clientX, y: e.clientY };
            over.style.cursor = 'grabbing';
        } else if (tool === 'pen' || tool === 'eraser') {
            const eraser = tool === 'eraser';
            const o = newShape('pen', [{ x: raw.x, y: raw.y }]);
            o.color = eraser ? '#ffffff' : S.opts.color;
            o.width = eraser ? S.opts.width * ERASER_SCALE : S.opts.width;
            o.filled = true;
            S.drag = { kind: 'stroke', obj: o };
        } else if (tool === 'fill') {
            const x = clamp(Math.floor(raw.x), 0, S.W - 1), y = clamp(Math.floor(raw.y), 0, S.H - 1);
            renderBase();
            const bmp = floodFill(bctx, S.W, S.H, x, y, S.opts.color, S.opts.tol);
            if (bmp) addObject({ id: nextId++, type: 'fill', bmp, x: 0, y: 0, w: S.W, h: S.H, pts: null });
        } else if (tool === 'pick') {
            const x = clamp(Math.floor(raw.x), 0, S.W - 1), y = clamp(Math.floor(raw.y), 0, S.H - 1);
            renderBase();
            const [r, g, b] = bctx.getImageData(x, y, 1, 1).data;
            dotnet.invokeMethodAsync('OnColorPicked', rgbToHex(r, g, b));
        } else if (tool === 'polygon') {
            addPolygonPoint(snapPoint(raw), k);
        } else if (tool === 'select') {
            beginSelectDrag(raw, k, touch);
        } else {
            const p = snapPoint(raw);
            S.drag = { kind: 'shape', obj: newShape(tool, [p, { ...p }]), start: p };
        }
        schedule();
    }

    function addPolygonPoint(p, k) {
        if (!S.poly) {
            S.poly = { pts: [p] };
        } else {
            const pts = S.poly.pts;
            if (pts.length >= 3 && dist(p, pts[0]) <= HANDLE_PX * 1.2 * k) { finishPolygon(); return; }
            if (dist(p, pts[pts.length - 1]) <= 6 * k) {
                if (pts.length >= 3) finishPolygon();
                return;
            }
            pts.push(p);
        }
        notify();
    }

    function beginSelectDrag(raw, k, touch) {
        if (S.sel) {
            const h = hitHandle(S.sel, raw, k, touch);
            if (h) {
                const o = S.sel, c = centerOf(o);
                if (h.rot) {
                    S.drag = { kind: 'rotate', obj: o, c, startRot: o.rot || 0, startAngle: Math.atan2(raw.y - c.y, raw.x - c.x), moved: false };
                } else {
                    S.drag = { kind: 'resize', obj: o, h, c0: c, box: boxOf(o.pts), pts: o.pts.map(p => ({ ...p })), moved: false };
                }
                return;
            }
        }
        let hit = null;
        for (let i = S.objects.length - 1; i >= 0; i--) {
            if (hitTest(S.objects[i], raw, k)) { hit = S.objects[i]; break; }
        }
        select(hit);
        if (hit) S.drag = { kind: 'move', obj: hit, p0: raw, pts: hit.pts.map(p => ({ ...p })), moved: false };
    }

    function onMove(e) {
        if (S.pointers.has(e.pointerId)) S.pointers.set(e.pointerId, { x: e.clientX, y: e.clientY });
        if (S.pinch) { updatePinch(); return; }

        const raw = logical(e);
        S.hover = e.pointerType === 'touch' ? null : snapPoint(raw);
        if (S.poly && e.pointerType === 'touch') S.hover = null;
        const d = S.drag;
        if (d) {
            switch (d.kind) {
                case 'pan':
                    S.view.x += e.clientX - d.x;
                    S.view.y += e.clientY - d.y;
                    d.x = e.clientX;
                    d.y = e.clientY;
                    clampView();
                    applyView();
                    break;
                case 'stroke': {
                    const pts = d.obj.pts, last = pts[pts.length - 1];
                    if (dist(raw, last) >= 1) pts.push({ x: raw.x, y: raw.y });
                    break;
                }
                case 'shape': {
                    let p = snapPoint(raw);
                    if (S.opts.constrain) p = constrainShape(d.obj.type, d.start, p);
                    d.obj.pts[1] = p;
                    break;
                }
                case 'move': moveDrag(d, raw); break;
                case 'resize': resizeDrag(d, raw); break;
                case 'rotate': rotateDrag(d, raw); break;
            }
        }
        schedule();
    }

    function moveDrag(d, raw) {
        let dx = raw.x - d.p0.x, dy = raw.y - d.p0.y;
        if (S.opts.snap) {
            const b = boxOf(d.pts), stepX = S.W / S.opts.div, stepY = S.H / S.opts.div;
            const best = (lo, hi, step, delta) => {
                const a = Math.round((lo + delta) / step) * step - lo;
                const c = Math.round((hi + delta) / step) * step - hi;
                return Math.abs(a - delta) <= Math.abs(c - delta) ? a : c;
            };
            dx = best(b.x0, b.x1, stepX, dx);
            dy = best(b.y0, b.y1, stepY, dy);
        }
        dx = Math.round(dx);
        dy = Math.round(dy);
        if (dx || dy) d.moved = true;
        d.obj.pts = d.pts.map(p => ({ x: p.x + dx, y: p.y + dy }));
    }

    function resizeDrag(d, raw) {
        const o = d.obj, { h, box } = d;
        const q0 = o.rot ? rotate(raw, d.c0, -o.rot) : raw;
        const q = o.rot ? q0 : snapPoint(q0);
        const ax = h.hx < 0 ? box.x1 : h.hx > 0 ? box.x0 : (box.x0 + box.x1) / 2;
        const ay = h.hy < 0 ? box.y1 : h.hy > 0 ? box.y0 : (box.y0 + box.y1) / 2;
        const safe = v => Math.abs(v) < 1e-3 ? (v < 0 ? -1e-3 : 1e-3) : v;
        let sx = h.hx ? (q.x - ax) / safe(h.x - ax) : 1;
        let sy = h.hy ? (q.y - ay) / safe(h.y - ay) : 1;
        if (S.opts.constrain && h.hx && h.hy) {
            const s = Math.max(Math.abs(sx), Math.abs(sy));
            sx = Math.sign(sx || 1) * s;
            sy = Math.sign(sy || 1) * s;
        }
        o.pts = d.pts.map(p => ({ x: ax + (p.x - ax) * sx, y: ay + (p.y - ay) * sy }));
        if (o.rot) {
            // Rotation pivots on the box centre, which just moved: translate so the anchor stays put.
            const before = rotate({ x: ax, y: ay }, d.c0, o.rot);
            const after = rotate({ x: ax, y: ay }, centerOf(o), o.rot);
            for (const p of o.pts) { p.x += before.x - after.x; p.y += before.y - after.y; }
        }
        if (o.type !== 'pen') for (const p of o.pts) { p.x = Math.round(p.x); p.y = Math.round(p.y); }
        d.moved = true;
    }

    function rotateDrag(d, raw) {
        const o = d.obj;
        let rot = d.startRot + Math.atan2(raw.y - d.c.y, raw.x - d.c.x) - d.startAngle;
        const step = Math.PI / 12;
        const snapped = Math.round(rot / step) * step;
        // Snap to 15° steps when constraining, otherwise just magnetise to them.
        if (S.opts.constrain || Math.abs(rot - snapped) < 0.045) rot = snapped;
        o.rot = rot;
        d.moved = true;
    }

    function onUp(e) {
        S.pointers.delete(e.pointerId);
        if (S.pointers.size === 0) { S.locked = false; S.pinch = null; }
        else if (S.pinch) { S.pinch = null; S.locked = true; }
        const d = S.drag;
        if (!d || S.locked) { S.drag = null; schedule(); return; }
        S.drag = null;
        updateCursor();
        switch (d.kind) {
            case 'stroke': addObject(d.obj); break;
            case 'shape': {
                const [a, b] = d.obj.pts;
                const tiny = d.obj.type === 'line' ? dist(a, b) < 2 : Math.abs(a.x - b.x) < 2 && Math.abs(a.y - b.y) < 2;
                if (!tiny) addObject(d.obj);
                break;
            }
            case 'move':
            case 'resize':
            case 'rotate': if (d.moved) commit(); break;
        }
        schedule();
    }

    function onCancel(e) {
        S.pointers.delete(e.pointerId);
        if (S.pointers.size === 0) { S.locked = false; S.pinch = null; }
        S.drag = null;
        schedule();
    }

    // Two fingers: pinch to zoom, drag to pan. Any stroke in progress is dropped.
    function beginPinch() {
        S.drag = null;
        S.locked = true;
        const [a, b] = [...S.pointers.values()];
        const r = wrap.getBoundingClientRect();
        S.pinch = {
            d0: Math.max(dist(a, b), 1),
            z0: S.view.z, x0: S.view.x, y0: S.view.y,
            c0: { x: (a.x + b.x) / 2 - r.left, y: (a.y + b.y) / 2 - r.top }
        };
        schedule();
    }

    function updatePinch() {
        const p = S.pinch;
        const [a, b] = [...S.pointers.values()];
        if (!b) return;
        const r = wrap.getBoundingClientRect();
        const c = { x: (a.x + b.x) / 2 - r.left, y: (a.y + b.y) / 2 - r.top };
        const z = clamp(p.z0 * dist(a, b) / p.d0, 1, MAX_ZOOM);
        S.view.z = z;
        S.view.x = c.x - (p.c0.x - p.x0) * z / p.z0;
        S.view.y = c.y - (p.c0.y - p.y0) * z / p.z0;
        clampView();
        applyView();
        schedule();
        notify();
    }

    function onWheel(e) {
        const r = wrap.getBoundingClientRect();
        if (e.ctrlKey || e.metaKey) {
            e.preventDefault();
            zoomAt(e.clientX - r.left, e.clientY - r.top, Math.exp(-e.deltaY * 0.0025));
        } else if (S.view.z > 1) {
            e.preventDefault();
            S.view.x -= e.shiftKey ? e.deltaY : e.deltaX;
            S.view.y -= e.shiftKey ? 0 : e.deltaY;
            clampView();
            applyView();
        }
    }

    function onKey(e) {
        if (S.disposed) return;
        const t = e.target;
        if (t && (t.tagName === 'INPUT' || t.tagName === 'TEXTAREA' || t.tagName === 'SELECT' || t.isContentEditable)) return;
        const mod = e.ctrlKey || e.metaKey;
        const key = e.key.toLowerCase();

        if (e.type === 'keyup') {
            if (e.key === ' ') { S.space = false; updateCursor(); }
            return;
        }
        if (e.key === ' ') {
            if (!S.space) { S.space = true; updateCursor(); }
            if (S.hover) e.preventDefault();
            return;
        }
        if (mod && key === 'z') { e.preventDefault(); if (e.shiftKey) redo(); else undo(); return; }
        if (mod && key === 'y') { e.preventDefault(); redo(); return; }
        if (mod && key === 'd') { e.preventDefault(); duplicateSelected(); return; }
        if (mod || e.altKey) return;

        if (e.key === 'Escape') { cancelPolygon(); select(null); return; }
        if (e.key === 'Enter') { if (S.poly) { e.preventDefault(); finishPolygon(); } return; }
        if (e.key === 'Backspace' && S.poly) {
            e.preventDefault();
            S.poly.pts.pop();
            if (!S.poly.pts.length) S.poly = null;
            notify();
            schedule();
            return;
        }
        if (e.key === 'Delete' || e.key === 'Backspace') {
            if (S.sel) { e.preventDefault(); removeSelected(); }
            return;
        }
        const step = e.shiftKey ? 10 : 1;
        const arrows = { ArrowLeft: [-step, 0], ArrowRight: [step, 0], ArrowUp: [0, -step], ArrowDown: [0, step] };
        if (arrows[e.key] && S.sel) { e.preventDefault(); nudge(...arrows[e.key]); return; }
        if (HOTKEYS[key] && !e.shiftKey) dotnet.invokeMethodAsync('OnHotkeyTool', HOTKEYS[key]);
    }

    over.addEventListener('pointerdown', onDown);
    over.addEventListener('pointermove', onMove);
    over.addEventListener('pointerup', onUp);
    over.addEventListener('pointercancel', onCancel);
    over.addEventListener('pointerleave', () => { S.hover = null; schedule(); });
    over.addEventListener('contextmenu', e => e.preventDefault());
    over.addEventListener('wheel', onWheel, { passive: false });
    document.addEventListener('keydown', onKey);
    document.addEventListener('keyup', onKey);

    // ---- start

    const r = DEFAULT_RATIO.w / DEFAULT_RATIO.h;
    S.W = LONG_SIDE;
    S.H = Math.round(LONG_SIDE / r);
    applySize();
    updateCursor();
    commit();

    const api = {
        setTool,
        setOption,
        setRatio,
        undo,
        redo,
        clear: clearAll,
        deleteSelected: removeSelected,
        duplicate: duplicateSelected,
        toFront: () => reorderSelected(true),
        toBack: () => reorderSelected(false),
        flipH: () => flipSelected(true),
        flipV: () => flipSelected(false),
        zoomIn: () => zoomCenter(1.5),
        zoomOut: () => zoomCenter(1 / 1.5),
        zoomFit: () => { S.view = { z: 1, x: 0, y: 0 }; applyView(); schedule(); notify(); },
        finishPolygon,
        toPngBase64() {
            finishPolygon();
            renderBase();
            return base.toDataURL('image/png').split(',')[1];
        },
        dispose() {
            S.disposed = true;
            document.removeEventListener('keydown', onKey);
            document.removeEventListener('keyup', onKey);
            host.replaceChildren();
        }
    };
    const id = nextEditorId++;
    editors.set(id, api);
    return id;
}

// Blazor calls editor methods through this single entry point.
export function call(id, method, ...args) {
    const editor = editors.get(id);
    if (!editor) return undefined;
    const result = editor[method](...args);
    if (method === 'dispose') editors.delete(id);
    return result;
}
