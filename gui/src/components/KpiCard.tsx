import Card from "@mui/material/Card";
import CardActionArea from "@mui/material/CardActionArea";
import CardContent from "@mui/material/CardContent";
import Typography from "@mui/material/Typography";
import { useNavigate } from "react-router-dom";

interface KpiCardProps {
  label: string;
  value: string | number;
  caption?: string;
  linkTo?: string;
  color?: "primary" | "success" | "error" | "warning" | "info";
  testId?: string;
}

/** A dashboard headline number, optionally linking to the page behind it. */
export function KpiCard({ label, value, caption, linkTo, color, testId }: KpiCardProps) {
  const navigate = useNavigate();
  const content = (
    <CardContent>
      <Typography variant="overline" color="text.secondary">{label}</Typography>
      <Typography variant="h4" color={color ? `${color}.main` : undefined} data-testid={testId ? `${testId}-value` : undefined}>
        {value}
      </Typography>
      {caption && <Typography variant="caption" color="text.secondary">{caption}</Typography>}
    </CardContent>
  );

  return (
    // Full height so every card in a dashboard grid row is the same size; the grid controls the width.
    <Card variant="outlined" sx={{ height: "100%" }} data-testid={testId}>
      {linkTo ? <CardActionArea onClick={() => navigate(linkTo)}>{content}</CardActionArea> : content}
    </Card>
  );
}
