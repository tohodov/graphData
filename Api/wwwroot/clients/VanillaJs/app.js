import { GraphDataApplication } from "./src/application/GraphDataApplication.js";

const graphDataApplication = new GraphDataApplication({ document, window });
window.graphDataApplication = graphDataApplication;
graphDataApplication.start();
