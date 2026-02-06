(() => {
    const state = new Map();

    async function loadThree() {
        if (window.Bari3DThree) return window.Bari3DThree;
        const threeModule = await import("https://cdn.jsdelivr.net/npm/three@0.161.0/build/three.module.js");
        const controlsModule = await import("https://cdn.jsdelivr.net/npm/three@0.161.0/examples/jsm/controls/OrbitControls.js");
        window.Bari3DThree = { THREE: threeModule, OrbitControls: controlsModule.OrbitControls };
        return window.Bari3DThree;
    }

    function buildScene(THREE, OrbitControls, container, areaSlug) {
        const renderer = new THREE.WebGLRenderer({ antialias: true, alpha: true });
        renderer.setPixelRatio(window.devicePixelRatio || 1);
        renderer.setSize(container.clientWidth, container.clientHeight);
        container.appendChild(renderer.domElement);

        const scene = new THREE.Scene();
        scene.background = new THREE.Color(0x0b1220);

        const camera = new THREE.PerspectiveCamera(
            45,
            container.clientWidth / container.clientHeight,
            0.1,
            2000
        );
        camera.position.set(12, 10, 12);

        const controls = new OrbitControls(camera, renderer.domElement);
        controls.enableDamping = true;
        controls.dampingFactor = 0.08;
        controls.screenSpacePanning = true;
        controls.minDistance = 2;
        controls.maxDistance = 200;

        const grid = new THREE.GridHelper(40, 40, 0x334155, 0x1f2937);
        grid.position.y = 0;
        scene.add(grid);

        const axes = new THREE.AxesHelper(5);
        axes.position.y = 0.01;
        scene.add(axes);

        const ambient = new THREE.AmbientLight(0xffffff, 0.65);
        scene.add(ambient);

        const dirLight = new THREE.DirectionalLight(0xffffff, 0.7);
        dirLight.position.set(10, 18, 8);
        scene.add(dirLight);

        const areaMaterial = new THREE.MeshStandardMaterial({
            color: 0x2563eb,
            roughness: 0.4,
            metalness: 0.1,
            transparent: true,
            opacity: 0.75,
        });
        const areaGeometry = new THREE.BoxGeometry(12, 3, 8);
        const areaMesh = new THREE.Mesh(areaGeometry, areaMaterial);
        areaMesh.position.set(0, 1.5, 0);
        scene.add(areaMesh);

        const label = document.createElement("div");
        label.textContent = `Área: ${areaSlug}`;
        label.style.position = "absolute";
        label.style.top = "12px";
        label.style.left = "12px";
        label.style.color = "#e2e8f0";
        label.style.fontSize = "0.85rem";
        label.style.padding = "4px 8px";
        label.style.background = "rgba(15, 23, 42, 0.7)";
        label.style.borderRadius = "8px";
        label.style.pointerEvents = "none";
        container.appendChild(label);

        const animate = () => {
            const entry = state.get(container);
            if (!entry) return;
            controls.update();
            renderer.render(scene, camera);
            entry.animationId = requestAnimationFrame(animate);
        };
        animate();

        const handleResize = () => {
            const width = container.clientWidth;
            const height = container.clientHeight;
            if (!width || !height) return;
            camera.aspect = width / height;
            camera.updateProjectionMatrix();
            renderer.setSize(width, height);
        };

        window.addEventListener("resize", handleResize);

        state.set(container, {
            renderer,
            camera,
            scene,
            controls,
            animationId: null,
            resizeHandler: handleResize,
            label,
        });
    }

    async function initArea3D(containerId, areaSlug) {
        const container = document.getElementById(containerId);
        if (!container) return;

        if (state.has(container)) return;
        const { THREE, OrbitControls } = await loadThree();
        buildScene(THREE, OrbitControls, container, areaSlug);
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
        if (entry.renderer.domElement.parentNode) {
            entry.renderer.domElement.parentNode.removeChild(entry.renderer.domElement);
        }
        if (entry.label && entry.label.parentNode) {
            entry.label.parentNode.removeChild(entry.label);
        }
        state.delete(container);
    }

    window.Bari3D = { initArea3D, dispose };
})();
