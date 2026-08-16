using Sidwell.Backend.Application.Contracts.Application;

namespace Sidwell.Backend.Application.Implementations;

public sealed class AlgorithmMetadataService : IAlgorithmMetadataService
{
    private static readonly IReadOnlyDictionary<string, AlgorithmMetadata> Metadata =
        new Dictionary<string, AlgorithmMetadata>(StringComparer.OrdinalIgnoreCase)
        {
            ["piotroski"] = new(
                Formula: "F-score = sum of 9 binary criteria (profitability / leverage / efficiency)",
                Definition: "Ranks financial strength on a 0–9 scale using annual fundamentals. Score ≥8 is strong, ≤3 is weak.",
                How: "Higher is better. Score ≥7 signals a financially sound company worth investigating; ≤3 suggests financial deterioration."
            ),
            ["altman_z"] = new(
                Formula: "Z = 1.2×(WC/TA) + 1.4×(RE/TA) + 3.3×(EBIT/TA) + 0.6×(MktCap/TL) + 1.0×(Revenue/TA)",
                Definition: "Bankruptcy prediction model using five financial ratios. Z > 2.99 = safe zone, 1.81–2.99 = grey zone, < 1.81 = distress zone.",
                How: "Higher is safer. Z < 1.81 is a red flag for financial distress; Z > 2.99 suggests strong solvency."
            ),
            ["greenblatt"] = new(
                Formula: "Magic Formula: rank by earnings yield (EBIT/EV) combined with rank by ROIC (EBIT / invested capital)",
                Definition: "Joel Greenblatt's system for finding good businesses at cheap prices by combining profitability and valuation rankings.",
                How: "Higher combined rank is better. Look for high ROIC (durable competitive advantage) paired with high earnings yield (cheap valuation)."
            ),
            ["dcf"] = new(
                Formula: "Intrinsic value = PV(5-yr FCF at WACC 9%) + PV(terminal value at 2.5% perpetual growth); score = % upside",
                Definition: "Discounted cash flow model estimating intrinsic per-share value using free cash flow and earnings growth rate.",
                How: "Positive score means the model price is above the current price (undervalued). Negative score means the stock trades above model estimate."
            ),
            ["pe_projections"] = new(
                Formula: "Fair value = forward EPS × sector P/E (default 20); score = 12M projected return %",
                Definition: "Projects stock price based on EPS growth applied to a sector-average P/E multiple over multiple time horizons.",
                How: "Positive score means the model sees upside over 12 months. Treat as a valuation sanity check, not a precise forecast."
            ),
            ["peg"] = new(
                Formula: "PEG = (P/E ratio) ÷ EPS growth rate (%)",
                Definition: "Adjusts the P/E ratio for earnings growth, giving a growth-normalized valuation signal.",
                How: "PEG < 1 suggests undervalued relative to growth; 1–2 is fairly priced; > 2 may indicate an expensive stock."
            ),
            ["ddm"] = new(
                Formula: "Intrinsic value = annual dividend ÷ (required return − dividend growth); score = % upside vs current price",
                Definition: "Gordon Growth Model valuing the stock based on its expected dividend stream discounted at a 10% required return.",
                How: "Positive upside means the dividend stream supports a higher price than the current market. Useful for dividend-paying stocks."
            ),
            ["momentum"] = new(
                Formula: "Composite = 30%×3M + 40%×6M + 30%×12M − 10%×1M returns; scaled to −100 / +100",
                Definition: "Measures recent price trend strength across multiple time windows using a weighted return composite.",
                How: "Score > 0 signals upward momentum; < 0 signals downward pressure. Strong positive momentum often persists short-term."
            ),
            ["accruals"] = new(
                Formula: "Accrual ratio = (net income − operating cash flow) ÷ average total assets",
                Definition: "Measures earnings quality by comparing reported profits to actual cash generated. High accruals suggest inflated earnings.",
                How: "Lower (more negative) is better. Ratio > 0.10 is a red flag: earnings may be overstated relative to operating cash flow."
            ),
            ["gross_profitability"] = new(
                Formula: "GP/Assets = gross profit ÷ total assets",
                Definition: "Robert Novy-Marx quality factor measuring how efficiently a firm converts assets into gross profit.",
                How: "Higher ratio signals a more capital-efficient, durable business. Ratio > 0.33 is generally considered high quality."
            ),
            ["beneish_m"] = new(
                Formula: "M = −4.84 + 0.92×DSRI + 0.53×GMI + 0.40×AQI + 0.89×SGI + 0.12×DEPI − 0.17×SGAI + 4.68×TATA − 0.33×LVGI",
                Definition: "Statistical model detecting earnings manipulation using 8 accounting ratios. M > −1.78 flags probable manipulation.",
                How: "Lower M-score is better. M > −1.78 is a strong red flag; −2.22 to −1.78 is a grey zone; < −2.22 suggests clean earnings."
            ),
            ["acquirers"] = new(
                Formula: "Acquirer's Multiple = EV / Operating Earnings (EBIT)",
                Definition: "Tobias Carlisle's deep value metric identifying undervalued companies based on Enterprise Value to Operating Earnings.",
                How: "Lower multiple is better. Indicates cheap valuation relative to cash-generating operating earnings."
            ),
            ["montier_c"] = new(
                Formula: "C-score = sum of 6 binary warning checks across accounting and divergence indicators",
                Definition: "James Montier's C-score designed to identify companies cooking their books or facing operational degradation.",
                How: "Lower is safer. A score of 0–1 is healthy; scores ≥ 4 indicate significant accounting and red-flag risks."
            ),
            ["mohanram_g"] = new(
                Formula: "G-score = sum of 8 binary criteria tailored for growth and intangible asset-heavy firms",
                Definition: "Partha Mohanram's G-score distinguishing fundamental winners from losers among low book-to-market (growth) stocks.",
                How: "Higher score is better. G-score ≥ 6 indicates a robust growth company with sustainable competitive advantages."
            ),
        };

    public IReadOnlyDictionary<string, AlgorithmMetadata> GetAll() => Metadata;
}
