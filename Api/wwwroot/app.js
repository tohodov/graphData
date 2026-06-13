import { GraphViewer } from "./src/GraphViewer.js";

const graphViewer = new GraphViewer({ document, window });
window.graphViewer = graphViewer;
graphViewer.start();
