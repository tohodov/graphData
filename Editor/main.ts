import { GraphViewer } from "./src/GraphViewer.js";

declare global {
  interface Window {
    graphViewer: GraphViewer;
  }
}

const graphViewer = new GraphViewer({ document, window });
window.graphViewer = graphViewer;
void graphViewer.start();
