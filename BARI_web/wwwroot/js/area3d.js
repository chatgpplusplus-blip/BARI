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

        host.textContent = ""; // ✅ limpia SOLO lo que tú agregas
        container.style.position = container.style.position || "relative";

        const renderer = new THREE.WebGLRenderer({ antialias: true, alpha: true });
        renderer.setPixelRatio(window.devicePixelRatio || 1);
        renderer.setSize(container.clientWidth, container.clientHeight);
        host.appendChild(renderer.domElement);

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

        (data.blocks || []).forEach((b) => {
            const w = Number(b.w ?? 0);
            const l = Number(b.l ?? 0);
            const h = Number(b.h ?? 0);

            if (!w || !l || !h) return;

            const geom = new THREE.BoxGeometry(w, h, l);
            const mat = baseBlockMaterial.clone();
            mat.color = new THREE.Color(b.color || "#2563eb");

            const mesh = new THREE.Mesh(geom, mat);
            mesh.position.set(
                (b.x - centerX) + w / 2,
                h / 2,
                (b.y - centerZ) + l / 2
            );
            areaGroup.add(mesh);
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
        host.appendChild(label);

        const entry = {
            renderer,
            camera,
            scene,
            controls,
            animationId: null,
            resizeHandler: null,
            label,
            host
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
