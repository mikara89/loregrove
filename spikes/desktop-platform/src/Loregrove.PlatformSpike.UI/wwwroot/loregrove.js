window.loregroveTheme = {
    get: () => localStorage.getItem("loregrove-theme") ?? "system",
    set: value => localStorage.setItem("loregrove-theme", value)
};

window.loregroveGraph = (() => {
    const graphs = new Map();
    return {
        render: (elementId, model, dotnet) => {
            const elements = [
                ...model.nodes.map(node => ({ data: { id: node.id, label: node.label, category: node.category } })),
                ...model.edges.map(edge => ({ data: { id: edge.id, source: edge.source, target: edge.target } }))
            ];
            const started = performance.now();
            const graph = cytoscape({
                container: document.getElementById(elementId), elements,
                style: [
                    { selector: "node", style: { "background-color": "#79a98f", "label": "data(label)", "color": "#f5f5f5", "font-size": 8, "text-valign": "bottom", "text-margin-y": 5 } },
                    { selector: "edge", style: { "width": 1, "line-color": "#547565", "opacity": .5, "curve-style": "bezier" } },
                    { selector: ":selected", style: { "background-color": "#e6a34a", "line-color": "#e6a34a" } }
                ],
                layout: { name: "cose", animate: false, nodeRepulsion: 6500, idealEdgeLength: 70 }
            });
            graph.on("tap", "node", event => dotnet.invokeMethodAsync("SelectNode", event.target.id(), event.target.data("label")));
            graphs.set(elementId, { graph, dotnet, renderMs: performance.now() - started });
        },
        metrics: elementId => {
            const entry = graphs.get(elementId);
            return entry ? { nodes: entry.graph.nodes().length, edges: entry.graph.edges().length, renderMs: entry.renderMs } : null;
        },
        selectFirst: elementId => { const entry = graphs.get(elementId); const node = entry?.graph.nodes()[0]; if (!node) return false; node.select(); return entry.dotnet.invokeMethodAsync("SelectNode", node.id(), node.data("label")); },
        interactionProbe: elementId => {
            const graph = graphs.get(elementId)?.graph;
            if (!graph) return null;
            const started = performance.now(); graph.zoom(1.2); graph.pan({ x: 12, y: 8 });
            return performance.now() - started;
        },
        dispose: elementId => { graphs.get(elementId)?.graph.destroy(); graphs.delete(elementId); }
    };
})();

window.loregroveDrop = (() => {
    const handlers = new Map();
    return {
        register: (elementId, dotnet) => {
            const element = document.getElementById(elementId);
            const handler = event => {
                event.preventDefault();
                const files = [...event.dataTransfer.files].map(file => ({ name: file.name, size: file.size, type: file.type }));
                dotnet.invokeMethodAsync("FilesDropped", files);
            };
            element.addEventListener("drop", handler);
            handlers.set(elementId, { element, handler });
        },
        dispose: elementId => {
            const entry = handlers.get(elementId);
            if (entry) entry.element.removeEventListener("drop", entry.handler);
            handlers.delete(elementId);
        }
    };
})();

window.addEventListener("keydown", event => {
    if (!(event.ctrlKey || event.metaKey)) return;
    if (event.key.toLowerCase() === "f") {
        const field = document.querySelector("fluent-text-input, input[type=search], input[type=text]");
        if (field) { event.preventDefault(); field.focus(); }
        else { const search = document.querySelector("[href='/search']"); if (search) { event.preventDefault(); search.click(); } }
    }
    if (event.key.toLowerCase() === "o") {
        const button = document.getElementById("pick-files");
        if (button) { event.preventDefault(); button.click(); }
        else { const link = document.querySelector("[href='/settings']"); if (link) { event.preventDefault(); link.click(); } }
    }
});

window.addEventListener("drop", event => event.preventDefault());
window.addEventListener("dragover", event => event.preventDefault());
