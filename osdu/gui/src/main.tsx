import "@fontsource-variable/inter";
import "@fontsource-variable/jetbrains-mono";
import "./index.css";
import { renderApp } from "@/bootstrap";
import { osduDeliveryModule } from "./module";

// SQLFlow's GUI with the OSDU Delivery module installed: its pages, navigation, per-kind panels and branding.
renderApp([osduDeliveryModule]);
