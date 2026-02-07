(() => {
    const state = new Map();

    async function loadThree() {
        if (window.Bari3DThree) return window.Bari3DThree;

        const THREE = await import("three");
        const { OrbitControls } = await import("three/addons/controls/OrbitControls.js");

        window.Bari3DThree = { THREE, OrbitControls };
        return window.Bari3DThree;
    }

    function computeBounds(data) {
        let minX = Infinity;
        let minZ = Infinity;
        let maxX = -Infinity;
        let maxZ = -Infinity;

        (data.polys || []).forEach((poly) => {
            (poly || []).forEach((pt) => {
                minX = Math.min(minX, pt.x);
                maxX = Math.max(maxX, pt.x);
                minZ = Math.min(minZ, pt.y);
                maxZ = Math.max(maxZ, pt.y);
            });
        });

        if (!Number.isFinite(minX)) {
            minX = 0;
            minZ = 0;
            maxX = 1;
            maxZ = 1;
        }
        return { minX, minZ, maxX, maxZ };
    }

    function buildScene(THREE, OrbitControls, container, data) {
        // Usa un host interno para no romper el DOM que Blazor controla
        const host = container.querySelector(".bari-3d-host");
        if (!host) return;

        let layer = host.querySelector(".bari-3d-layer");
        if (!layer) {
            layer = document.createElement("div");
            layer.className = "bari-3d-layer";
            layer.style.width = "100%";
            layer.style.height = "100%";
            layer.style.position = "relative";
            host.appendChild(layer);
        }

        layer.replaceChildren(); // ✅ limpia SOLO lo que tú agregas
        container.style.position = container.style.position || "relative";

        const renderer = new THREE.WebGLRenderer({ antialias: true, alpha: true });
        renderer.setPixelRatio(window.devicePixelRatio || 1);
        renderer.setSize(container.clientWidth, container.clientHeight);
        layer.appendChild(renderer.domElement);

        const scene = new THREE.Scene();
        scene.background = new THREE.Color(0x0b1220);

        const camera = new THREE.PerspectiveCamera(
            45,
            container.clientWidth / container.clientHeight,
            0.1,
            2000
        );
        camera.position.set(16, 16, 16);

        const controls = new OrbitControls(camera, renderer.domElement);
        controls.enableDamping = true;
        controls.dampingFactor = 0.08;
        controls.screenSpacePanning = true;
        controls.minDistance = 2;
        controls.maxDistance = 200;

        // Helpers
        const grid = new THREE.GridHelper(80, 80, 0x334155, 0x1f2937);
        grid.position.y = 0;
        scene.add(grid);

        const axes = new THREE.AxesHelper(5);
        axes.position.y = 0.01;
        scene.add(axes);

        // Luces (sin esto, todo negro)
        const ambient = new THREE.AmbientLight(0xffffff, 0.65);
        scene.add(ambient);

        const dirLight = new THREE.DirectionalLight(0xffffff, 0.7);
        dirLight.position.set(10, 18, 8);
        scene.add(dirLight);

        const areaGroup = new THREE.Group();
        scene.add(areaGroup);

        const bounds = computeBounds(data);
        const centerX = (bounds.minX + bounds.maxX) / 2;
        const centerZ = (bounds.minZ + bounds.maxZ) / 2;

        // Apunta la cámara al centro
        controls.target.set(0, 0, 0);
        camera.lookAt(0, 0, 0);

        const areaHeight = data.areaHeight || 3;

        const areaMaterial = new THREE.MeshStandardMaterial({
            color: 0x94a3b8,
            roughness: 0.55,
            metalness: 0.05,
            transparent: true,
            opacity: 0.6,
        });

        (data.polys || []).forEach((poly) => {
            if (!poly || poly.length < 3) return;
            const shape = new THREE.Shape();

            poly.forEach((pt, idx) => {
                const x = pt.x - centerX;
                const y = -(pt.y - centerZ); // ✅ FIX: invierte Y para que el área y los objetos coincidan en Z
                if (idx === 0) shape.moveTo(x, y);
                else shape.lineTo(x, y);
            });

            const geom = new THREE.ExtrudeGeometry(shape, {
                depth: areaHeight,
                bevelEnabled: false,
            });
            geom.rotateX(-Math.PI / 2);

            const mesh = new THREE.Mesh(geom, areaMaterial);
            mesh.position.y = 0;
            areaGroup.add(mesh);
        });

        const baseBlockMaterial = new THREE.MeshStandardMaterial({
            color: 0x2563eb,
            roughness: 0.4,
            metalness: 0.1,
        });

        const shelfMaterial = new THREE.MeshStandardMaterial({
            color: 0x3b82f6,
            roughness: 0.45,
            metalness: 0.05,
        });

        const boxMaterial = new THREE.MeshStandardMaterial({
            color: 0x60a5fa,
            roughness: 0.5,
            metalness: 0.05,
        });

        const clamp = (value, min, max) => Math.min(Math.max(value, min), max);

        const parseDimensions = (raw) => {
            if (!raw || typeof raw !== "string") return null;
            const nums = raw
                .replace(/[^0-9.,xX*\\s-]/g, " ")
                .split(/[xX*\\s,]+/)
                .map((v) => parseFloat(v.replace(",", ".")))
                .filter((v) => Number.isFinite(v) && v > 0);
            if (nums.length < 2) return null;
            const dims = nums.slice(0, 3);
            const max = Math.max(...dims);
            if (max > 3) {
                return dims.map((v) => v / 100);
            }
            return dims;
        };

        const buildShelfUnit = (b, mat, group) => {
            const w = Number(b.w ?? 0);
            const l = Number(b.l ?? 0);
            const h = Number(b.h ?? 0);
            if (!w || !l || !h) return;

            const longAxisIsX = w >= l;
            const longSide = longAxisIsX ? w : l;
            const shortSide = longAxisIsX ? l : w;
            const thickness = clamp(shortSide * 0.08, 0.02, 0.08);

            const innerLong = longSide - thickness * 2;
            const innerShort = shortSide - thickness * 2;
            const innerHeight = Math.max(0.2, h - thickness * 2);

            const levels = Math.max(1, Math.round(b.levels || 0) || 3);
            const levelHeight = innerHeight / levels;

            const shelfGroup = new THREE.Group();

            const baseGeom = longAxisIsX
                ? new THREE.BoxGeometry(longSide, thickness, shortSide)
                : new THREE.BoxGeometry(shortSide, thickness, longSide);
            const baseMesh = new THREE.Mesh(baseGeom, mat);
            baseMesh.position.set(0, thickness / 2, 0);
            shelfGroup.add(baseMesh);

            const topMesh = new THREE.Mesh(baseGeom, mat);
            topMesh.position.set(0, h - thickness / 2, 0);
            shelfGroup.add(topMesh);

            const endGeom = longAxisIsX
                ? new THREE.BoxGeometry(thickness, h, shortSide)
                : new THREE.BoxGeometry(shortSide, h, thickness);
            const endOffset = longSide / 2 - thickness / 2;

            if (longAxisIsX) {
                const endA = new THREE.Mesh(endGeom, mat);
                endA.position.set(-endOffset, h / 2, 0);
                shelfGroup.add(endA);

                const endB = new THREE.Mesh(endGeom, mat);
                endB.position.set(endOffset, h / 2, 0);
                shelfGroup.add(endB);
            } else {
                const endA = new THREE.Mesh(endGeom, mat);
                endA.position.set(0, h / 2, -endOffset);
                shelfGroup.add(endA);

                const endB = new THREE.Mesh(endGeom, mat);
                endB.position.set(0, h / 2, endOffset);
                shelfGroup.add(endB);
            }

            for (let i = 1; i < levels; i += 1) {
                const shelf = new THREE.Mesh(baseGeom, mat);
                shelf.position.set(0, thickness + levelHeight * i, 0);
                shelfGroup.add(shelf);
            }

            const bayCount = clamp(Math.round(longSide / 0.8), 2, 5);
            const dividerHeight = h - thickness * 2;
            const dividerGeom = longAxisIsX
                ? new THREE.BoxGeometry(thickness, dividerHeight, innerShort)
                : new THREE.BoxGeometry(innerShort, dividerHeight, thickness);

            for (let i = 1; i < bayCount; i += 1) {
                const offset = -innerLong / 2 + (innerLong / bayCount) * i;
                const divider = new THREE.Mesh(dividerGeom, mat);
                if (longAxisIsX) {
                    divider.position.set(offset, h / 2, 0);
                } else {
                    divider.position.set(0, h / 2, offset);
                }
                shelfGroup.add(divider);
            }

            const boxes = Array.isArray(b.boxes) ? b.boxes : [];
            const boxesByLevel = new Map();
            boxes.forEach((box) => {
                const level = Math.max(1, Math.round(box.level || 1));
                if (!boxesByLevel.has(level)) boxesByLevel.set(level, []);
                boxesByLevel.get(level).push(box);
            });

            boxesByLevel.forEach((items, level) => {
                const y = thickness + levelHeight * (level - 0.5);
                const columns = Math.max(1, bayCount);
                const rows = Math.max(1, Math.ceil(items.length / columns));
                const cellLong = innerLong / columns;
                const cellShort = innerShort / rows;

                items.forEach((box, index) => {
                    const dims = parseDimensions(box.dimensions);
                    const rawW = dims?.[0] ?? cellLong * 0.7;
                    const rawL = dims?.[1] ?? cellShort * 0.7;
                    const rawH = dims?.[2] ?? levelHeight * 0.7;

                    const bw = clamp(rawW, 0.05, cellLong * 0.9);
                    const bl = clamp(rawL, 0.05, cellShort * 0.9);
                    const bh = clamp(rawH, 0.05, levelHeight * 0.9);

                    const boxGeom = longAxisIsX
                        ? new THREE.BoxGeometry(bw, bh, bl)
                        : new THREE.BoxGeometry(bl, bh, bw);
                    const boxMesh = new THREE.Mesh(boxGeom, boxMaterial);

                    const col = index % columns;
                    const row = Math.floor(index / columns);
                    const offsetLong = -innerLong / 2 + cellLong * col + cellLong / 2;
                    const offsetShort = -innerShort / 2 + cellShort * row + cellShort / 2;

                    if (longAxisIsX) {
                        boxMesh.position.set(offsetLong, y, offsetShort);
                    } else {
                        boxMesh.position.set(offsetShort, y, offsetLong);
                    }
                    shelfGroup.add(boxMesh);
                });
            });

            shelfGroup.position.set(
                (b.x - centerX) + w / 2,
                0,
                (b.y - centerZ) + l / 2
            );
            group.add(shelfGroup);
        };

        (data.blocks || []).forEach((b) => {
            const w = Number(b.w ?? 0);
            const l = Number(b.l ?? 0);
            const h = Number(b.h ?? 0);

            if (!w || !l || !h) return;

            const mat = baseBlockMaterial.clone();
            mat.color = new THREE.Color(b.color || "#2563eb");

            if (b.isMeson) {
                buildShelfUnit(b, shelfMaterial.clone(), areaGroup);
                return;
            }

            const geom = new THREE.BoxGeometry(w, h, l);
            const mesh = new THREE.Mesh(geom, mat);
            mesh.position.set(
                (b.x - centerX) + w / 2,
                h / 2,
                (b.y - centerZ) + l / 2
            );
            areaGroup.add(mesh);

            const levelCount = Math.max(1, Number(b.levels ?? 0) || 1);
            const isMeson = b.mesonId || b.levels || b.boxCounts;
            if (!isMeson) return;

            const baseX = (b.x - centerX);
            const baseZ = (b.y - centerZ);
            const shelfThickness = Math.max(0.03, Math.min(0.08, h * 0.04));
            const shelfColor = mat.color.clone().multiplyScalar(0.7);
            const shelfMaterial = new THREE.MeshStandardMaterial({
                color: shelfColor,
                roughness: 0.55,
                metalness: 0.05,
            });

            for (let i = 1; i < levelCount; i += 1) {
                const shelfGeom = new THREE.BoxGeometry(w * 0.98, shelfThickness, l * 0.98);
                const shelf = new THREE.Mesh(shelfGeom, shelfMaterial);
                shelf.position.set(
                    baseX + w / 2,
                    (h / levelCount) * i,
                    baseZ + l / 2
                );
                areaGroup.add(shelf);
            }

            const boxCounts = Array.isArray(b.boxCounts) ? b.boxCounts : null;
            if (!boxCounts) return;

            const margin = Math.min(w, l) * 0.08;
            const innerW = Math.max(0.05, w - margin * 2);
            const innerL = Math.max(0.05, l - margin * 2);

            for (let level = 0; level < levelCount; level += 1) {
                const count = Number(boxCounts[level] ?? 0);
                if (!count) continue;

                const columns = Math.ceil(Math.sqrt(count));
                const rows = Math.ceil(count / columns);
                const cellW = innerW / columns;
                const cellL = innerL / rows;
                const boxW = cellW * 0.7;
                const boxL = cellL * 0.7;
                const cellHeight = h / levelCount;
                const yPadding = Math.min(0.06, cellHeight * 0.12);
                const boxHeight = Math.max(0.08, Math.min(cellHeight * 0.6, cellHeight - yPadding * 2));
                const levelBaseY = cellHeight * level;
                const boxY = levelBaseY + yPadding + boxHeight / 2;

                for (let i = 0; i < count; i += 1) {
                    const col = i % columns;
                    const row = Math.floor(i / columns);
                    const x = baseX + margin + cellW * (col + 0.5);
                    const z = baseZ + margin + cellL * (row + 0.5);
                    const boxGeom = new THREE.BoxGeometry(boxW, boxHeight, boxL);
                    const box = new THREE.Mesh(boxGeom, boxMaterial);
                    box.position.set(x, boxY, z);
                    areaGroup.add(box);
                }
            }
        });

        const doorMaterial = new THREE.MeshStandardMaterial({
            color: 0x22c55e,
            roughness: 0.3,
            metalness: 0.1,
        });

        const windowMaterial = new THREE.MeshStandardMaterial({
            color: 0x38bdf8,
            roughness: 0.2,
            metalness: 0.1,
            transparent: true,
            opacity: 0.6,
        });

        (data.doors || []).forEach((d) => {
            const length = Number(d.l ?? 0);
            if (!length) return;

            const thickness = 0.15;
            const height = 2.0;

            const isEW = d.orient === "E" || d.orient === "W";
            const geom = new THREE.BoxGeometry(
                isEW ? length : thickness,
                height,
                isEW ? thickness : length
            );

            const mesh = new THREE.Mesh(geom, doorMaterial);

            // ✅ FIX: en tu 2D la orientación es EJE (no dirección). Centro = inicio + largo/2 (siempre positivo).
            let x = (d.x - centerX);
            let z = (d.y - centerZ);

            if (isEW) {
                x += (length / 2);
            } else {
                z += (length / 2);
            }

            mesh.position.set(x, height / 2, z);
            areaGroup.add(mesh);
        });

        (data.windows || []).forEach((w) => {
            const length = Number(w.l ?? 0);
            if (!length) return;

            const thickness = 0.12;
            const height = 1.5;
            const base = 1.8;

            const isEW = w.orient === "E" || w.orient === "W";
            const geom = new THREE.BoxGeometry(
                isEW ? length : thickness,
                height,
                isEW ? thickness : length
            );

            const mesh = new THREE.Mesh(geom, windowMaterial);

            // ✅ FIX: misma regla que puertas (orientación como eje)
            let x = (w.x - centerX);
            let z = (w.y - centerZ);

            if (isEW) {
                x += (length / 2);
            } else {
                z += (length / 2);
            }

            mesh.position.set(x, base + height / 2, z);
            areaGroup.add(mesh);
        });


        // Label
        const label = document.createElement("div");
        label.textContent = `Área: ${data.areaId ?? ""}`;
        label.style.position = "absolute";
        label.style.top = "12px";
        label.style.left = "12px";
        label.style.color = "#e2e8f0";
        label.style.fontSize = "0.85rem";
        label.style.padding = "4px 8px";
        label.style.background = "rgba(15, 23, 42, 0.7)";
        label.style.borderRadius = "8px";
        label.style.pointerEvents = "none";
        layer.appendChild(label);

        const entry = {
            renderer,
            camera,
            scene,
            controls,
            animationId: null,
            resizeHandler: null,
            label,
            host,
            layer
        };


        const handleResize = () => {
            const width = container.clientWidth;
            const height = container.clientHeight;
            if (!width || !height) return;
            camera.aspect = width / height;
            camera.updateProjectionMatrix();
            renderer.setSize(width, height);
        };

        entry.resizeHandler = handleResize;

        state.set(container, entry);
        window.addEventListener("resize", handleResize);

        // render inicial
        renderer.render(scene, camera);

        const animate = () => {
            const current = state.get(container);
            if (!current) return;
            controls.update();
            renderer.render(scene, camera);
            current.animationId = requestAnimationFrame(animate);
        };
        animate();
    }

    async function initArea3D(containerId, data) {
        const container = document.getElementById(containerId);
        if (!container) return;

        if (state.has(container)) return;
        const { THREE, OrbitControls } = await loadThree();
        buildScene(THREE, OrbitControls, container, data);
    }

    function dispose(containerId) {
        const container = document.getElementById(containerId);
        if (!container) return;

        const entry = state.get(container);
        if (!entry) return;

        cancelAnimationFrame(entry.animationId);
        window.removeEventListener("resize", entry.resizeHandler);

        entry.controls.dispose();
        entry.renderer.dispose();

        entry.renderer?.domElement?.remove?.();
        entry.label?.remove?.();
        entry.layer?.replaceChildren?.();


        state.delete(container);
    }

    async function initArea3DSafe(containerId, data) {
        if (!window.Bari3D || typeof window.Bari3D.initArea3D !== "function") {
            return false;
        }
        await window.Bari3D.initArea3D(containerId, data);
        return true;
    }

    window.Bari3D = { initArea3D, dispose };
    window.Bari3DInitSafe = initArea3DSafe;

    console.log("✅ area3d.js cargado", typeof window.Bari3DInitSafe);
})();
