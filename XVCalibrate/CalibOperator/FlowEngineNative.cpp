#include "FlowEngineNative.h"
#include <cstring>
#include <fstream>
#include <opencv2/opencv.hpp>
#include <unordered_map>
#include <queue>
#include <sstream>
#include <iomanip>
#include <vector>

namespace {

struct ContoursData {
    std::vector<int> flatX;
    std::vector<int> flatY;
    std::vector<int> lengths;
    int count = 0;
};

struct Value {
    enum class Kind { None, Int, Double, Image, Points, Contours, Transform, String } kind = Kind::None;
    int i = 0;
    double d = 0.0;
    cv::Mat img;
    std::vector<Point2D> points;
    std::vector<int> barIds;
    ContoursData contours;
    AffineTransform trans{};
    std::string str;
};

struct NodeDef {
    std::string id;
    std::string type;
    std::unordered_map<std::string, std::string> params;
};

struct ConnDef {
    std::string fromNodeId;
    std::string fromPort;
    std::string toNodeId;
    std::string toPort;
};

struct NativeFlowEngineImpl {
    std::vector<NodeDef> nodes;
    std::vector<ConnDef> conns;
    std::unordered_map<std::string, std::unordered_map<std::string, Value>> outputs;
    std::unordered_map<std::string, std::string> nodeErrors;
    std::string lastError;
    std::string lastReportJson;
};

static std::string NodeParam(const NodeDef& node, const char* key, const char* defVal = "") {
    auto it = node.params.find(key);
    if (it == node.params.end()) return defVal ? defVal : "";
    return it->second;
}

static int ToInt(const std::string& s, int defVal) {
    try { return std::stoi(s); } catch (...) { return defVal; }
}

static double ToDouble(const std::string& s, double defVal) {
    try { return std::stod(s); } catch (...) { return defVal; }
}

static cv::Mat EnsureGray(const cv::Mat& src) {
    if (src.empty()) return src;
    if (src.channels() == 1) return src;
    cv::Mat gray;
    cv::cvtColor(src, gray, cv::COLOR_BGR2GRAY);
    return gray;
}

static cv::Mat EnsureBgr(const cv::Mat& src) {
    if (src.empty()) return src;
    if (src.channels() == 3) return src;
    cv::Mat bgr;
    cv::cvtColor(src, bgr, cv::COLOR_GRAY2BGR);
    return bgr;
}

static Value MakeImage(const cv::Mat& m) {
    Value v;
    v.kind = Value::Kind::Image;
    v.img = m.clone();
    return v;
}

static Value MakeContours(const std::vector<std::vector<cv::Point>>& contours) {
    Value v;
    v.kind = Value::Kind::Contours;
    for (const auto& c : contours) {
        if (c.size() < 2) continue;
        v.contours.lengths.push_back((int)c.size());
        for (const auto& p : c) {
            v.contours.flatX.push_back(p.x);
            v.contours.flatY.push_back(p.y);
        }
    }
    v.contours.count = (int)v.contours.lengths.size();
    return v;
}

static std::vector<std::vector<cv::Point>> ToContours(const ContoursData& cd) {
    std::vector<std::vector<cv::Point>> out;
    int offset = 0;
    for (int len : cd.lengths) {
        if (len <= 0 || offset + len > (int)cd.flatX.size() || offset + len > (int)cd.flatY.size()) break;
        std::vector<cv::Point> c;
        c.reserve(len);
        for (int i = 0; i < len; ++i) c.emplace_back(cd.flatX[offset + i], cd.flatY[offset + i]);
        out.push_back(std::move(c));
        offset += len;
    }
    return out;
}

static Value InputOf(NativeFlowEngineImpl* e, const std::string& nodeId, const std::string& portName) {
    for (const auto& c : e->conns) {
        if (c.toNodeId == nodeId && c.toPort == portName) {
            auto nit = e->outputs.find(c.fromNodeId);
            if (nit == e->outputs.end()) continue;
            auto pit = nit->second.find(c.fromPort);
            if (pit == nit->second.end()) continue;
            return pit->second;
        }
    }
    return Value{};
}

static std::vector<std::string> TopoSort(NativeFlowEngineImpl* e) {
    std::unordered_map<std::string, int> indeg;
    for (const auto& n : e->nodes) indeg[n.id] = 0;
    for (const auto& c : e->conns) if (indeg.count(c.toNodeId)) indeg[c.toNodeId]++;
    std::queue<std::string> q;
    for (const auto& kv : indeg) if (kv.second == 0) q.push(kv.first);
    std::vector<std::string> order;
    while (!q.empty()) {
        std::string id = q.front(); q.pop();
        order.push_back(id);
        for (const auto& c : e->conns) {
            if (c.fromNodeId != id) continue;
            if (!indeg.count(c.toNodeId)) continue;
            if (--indeg[c.toNodeId] == 0) q.push(c.toNodeId);
        }
    }
    if (order.size() != e->nodes.size()) {
        order.clear();
        for (const auto& n : e->nodes) order.push_back(n.id);
    }
    return order;
}

static const NodeDef* FindNode(const NativeFlowEngineImpl* e, const std::string& id) {
    for (const auto& n : e->nodes) if (n.id == id) return &n;
    return nullptr;
}

static bool ExecuteNode(NativeFlowEngineImpl* e, const NodeDef& n, std::string& err) {
    auto& out = e->outputs[n.id];
    out.clear();

    if (n.type == "load_image") {
        std::string path = NodeParam(n, "filePath", "");
        if (path.empty()) { err = "load_image: filePath empty"; return false; }
        cv::Mat img = cv::imread(path, cv::IMREAD_UNCHANGED);
        if (img.empty()) { err = "load_image: failed to read " + path; return false; }
        out["Image"] = MakeImage(img.channels() == 1 ? img : EnsureBgr(img));
        return true;
    }
    if (n.type == "grayscale") {
        Value vin = InputOf(e, n.id, "In");
        if (vin.kind != Value::Kind::Image) { err = "grayscale: missing In"; return false; }
        out["Out"] = MakeImage(EnsureGray(vin.img));
        return true;
    }
    if (n.type == "clahe") {
        Value vin = InputOf(e, n.id, "In");
        if (vin.kind != Value::Kind::Image) { err = "clahe: missing In"; return false; }
        cv::Mat gray = EnsureGray(vin.img);
        double clip = ToDouble(NodeParam(n, "clipLimit", "3.0"), 3.0);
        int tile = ToInt(NodeParam(n, "tileSize", "8"), 8);
        auto clahe = cv::createCLAHE(std::max(1.0, clip), cv::Size(std::max(2, tile), std::max(2, tile)));
        cv::Mat dst;
        clahe->apply(gray, dst);
        out["Out"] = MakeImage(dst);
        return true;
    }
    if (n.type == "pre_filter") {
        Value vin = InputOf(e, n.id, "In");
        if (vin.kind != Value::Kind::Image) { err = "pre_filter: missing In"; return false; }
        cv::Mat gray = EnsureGray(vin.img), dst;
        int k = ToInt(NodeParam(n, "ksize", "3"), 3); if ((k & 1) == 0) k += 1; if (k < 3) k = 3;
        std::string mode = NodeParam(n, "mode", "gaussian");
        if (mode == "median") cv::medianBlur(gray, dst, k); else cv::GaussianBlur(gray, dst, cv::Size(k, k), 0);
        out["Out"] = MakeImage(dst);
        return true;
    }
    if (n.type == "nlmeans") {
        Value vin = InputOf(e, n.id, "In");
        if (vin.kind != Value::Kind::Image) { err = "nlmeans: missing In"; return false; }
        cv::Mat gray = EnsureGray(vin.img), dst;
        float h = (float)ToDouble(NodeParam(n, "h", "12"), 12.0);
        int t = ToInt(NodeParam(n, "templateWindow", "7"), 7); if ((t & 1) == 0) t += 1;
        int s = ToInt(NodeParam(n, "searchWindow", "21"), 21); if ((s & 1) == 0) s += 1;
        cv::fastNlMeansDenoising(gray, dst, h, t, s);
        out["Out"] = MakeImage(dst);
        return true;
    }
    if (n.type == "sobel" || n.type == "scharr") {
        Value vin = InputOf(e, n.id, "In");
        if (vin.kind != Value::Kind::Image) { err = n.type + ": missing In"; return false; }
        cv::Mat gray = EnsureGray(vin.img), gx, gy, mag;
        if (n.type == "sobel") {
            cv::Sobel(gray, gx, CV_32F, 1, 0, 3);
            cv::Sobel(gray, gy, CV_32F, 0, 1, 3);
        } else {
            cv::Scharr(gray, gx, CV_32F, 1, 0);
            cv::Scharr(gray, gy, CV_32F, 0, 1);
        }
        cv::magnitude(gx, gy, mag);
        double th = ToDouble(NodeParam(n, "threshold", "48"), 48.0);
        cv::Mat edge;
        cv::threshold(mag, edge, th, 255, cv::THRESH_BINARY);
        edge.convertTo(edge, CV_8U);
        out["Edge"] = MakeImage(edge);
        return true;
    }
    if (n.type == "canny") {
        Value vin = InputOf(e, n.id, "In");
        if (vin.kind != Value::Kind::Image) { err = "canny: missing In"; return false; }
        cv::Mat gray = EnsureGray(vin.img), edge;
        cv::Canny(gray, edge, 40, 120);
        out["Edge"] = MakeImage(edge);
        return true;
    }
    if (n.type == "binarize") {
        Value vin = InputOf(e, n.id, "In");
        if (vin.kind != Value::Kind::Image) { err = "binarize: missing In"; return false; }
        cv::Mat gray = EnsureGray(vin.img), blur, bin;
        int b = ToInt(NodeParam(n, "blurSize", "7"), 7); if ((b & 1) == 0) b += 1;
        cv::GaussianBlur(gray, blur, cv::Size(std::max(3, b), std::max(3, b)), 0);
        cv::threshold(blur, bin, 0, 255, cv::THRESH_BINARY | cv::THRESH_OTSU);
        int m = ToInt(NodeParam(n, "morphSize", "5"), 5); if ((m & 1) == 0) m += 1;
        cv::Mat k = cv::getStructuringElement(cv::MORPH_ELLIPSE, cv::Size(std::max(3, m), std::max(3, m)));
        cv::morphologyEx(bin, bin, cv::MORPH_CLOSE, k);
        out["Out"] = MakeImage(bin);
        return true;
    }
    if (n.type == "gray_range_binary") {
        Value vin = InputOf(e, n.id, "In");
        if (vin.kind != Value::Kind::Image) { err = "gray_range_binary: missing In"; return false; }
        cv::Mat gray = EnsureGray(vin.img), bin;
        int lo = ToInt(NodeParam(n, "grayLow", "5"), 5);
        int hi = ToInt(NodeParam(n, "grayHigh", "50"), 50);
        cv::inRange(gray, lo, hi, bin);
        out["Out"] = MakeImage(bin);
        return true;
    }
    if (n.type == "find_contours") {
        Value vin = InputOf(e, n.id, "In");
        if (vin.kind != Value::Kind::Image) { err = "find_contours: missing In"; return false; }
        cv::Mat bin = EnsureGray(vin.img);
        std::vector<std::vector<cv::Point>> contours;
        cv::findContours(bin, contours, cv::RETR_EXTERNAL, cv::CHAIN_APPROX_NONE);
        std::sort(contours.begin(), contours.end(), [](const auto& a, const auto& b) { return cv::contourArea(a) > cv::contourArea(b); });
        cv::Mat vis = cv::Mat::zeros(bin.size(), CV_8UC1);
        for (size_t i = 0; i < contours.size(); ++i) cv::drawContours(vis, contours, (int)i, cv::Scalar(255), 1);
        Value c = MakeContours(contours);
        out["Contours"] = c;
        out["Out"] = MakeImage(vis);
        Value cnt; cnt.kind = Value::Kind::Int; cnt.i = (int)contours.size();
        out["Count"] = cnt;
        return true;
    }
    if (n.type == "create_mask") {
        Value vc = InputOf(e, n.id, "Contours");
        Value vi = InputOf(e, n.id, "In");
        if (vc.kind != Value::Kind::Contours) { err = "create_mask: missing Contours"; return false; }
        auto contours = ToContours(vc.contours);
        if (contours.empty()) { err = "create_mask: empty contours"; return false; }
        int idx = ToInt(NodeParam(n, "contourIdx", "-1"), -1);
        if (idx < 0 || idx >= (int)contours.size()) idx = 0;
        int w = vi.kind == Value::Kind::Image ? vi.img.cols : CALIB_IMAGE_WIDTH;
        int h = vi.kind == Value::Kind::Image ? vi.img.rows : CALIB_IMAGE_HEIGHT;
        cv::Mat mask = cv::Mat::zeros(h, w, CV_8UC1);
        cv::drawContours(mask, contours, idx, cv::Scalar(255), cv::FILLED);
        out["Mask"] = MakeImage(mask);
        return true;
    }
    if (n.type == "apply_mask") {
        Value img = InputOf(e, n.id, "Image");
        Value mask = InputOf(e, n.id, "Mask");
        if (img.kind != Value::Kind::Image || mask.kind != Value::Kind::Image) { err = "apply_mask: missing input"; return false; }
        cv::Mat src = img.img.clone();
        cv::Mat mk = EnsureGray(mask.img);
        cv::Mat outImg = cv::Mat::zeros(src.size(), src.type());
        src.copyTo(outImg, mk);
        out["Out"] = MakeImage(outImg);
        return true;
    }
    if (n.type == "detect_dark") {
        Value in = InputOf(e, n.id, "In");
        Value mask = InputOf(e, n.id, "Mask");
        if (in.kind != Value::Kind::Image || mask.kind != Value::Kind::Image) { err = "detect_dark: missing input"; return false; }
        cv::Mat gray = EnsureGray(in.img), mk = EnsureGray(mask.img), dark;
        int th = ToInt(NodeParam(n, "darkThreshold", "50"), 50);
        cv::threshold(gray, dark, th, 255, cv::THRESH_BINARY_INV);
        dark.setTo(0, mk == 0);
        out["Dark"] = MakeImage(dark);
        return true;
    }
    if (n.type == "morphology") {
        Value in = InputOf(e, n.id, "In");
        if (in.kind != Value::Kind::Image) { err = "morphology: missing In"; return false; }
        cv::Mat bin = EnsureGray(in.img);
        int k = ToInt(NodeParam(n, "kernelSize", "5"), 5); if ((k & 1) == 0) k += 1;
        cv::Mat ker = cv::getStructuringElement(cv::MORPH_ELLIPSE, cv::Size(std::max(3, k), std::max(3, k)));
        cv::morphologyEx(bin, bin, cv::MORPH_OPEN, ker);
        cv::morphologyEx(bin, bin, cv::MORPH_CLOSE, ker);
        out["Out"] = MakeImage(bin);
        return true;
    }
    if (n.type == "expand_edge") {
        Value dark = InputOf(e, n.id, "Dark");
        Value edge = InputOf(e, n.id, "Edge");
        if (dark.kind != Value::Kind::Image || edge.kind != Value::Kind::Image) { err = "expand_edge: missing input"; return false; }
        cv::Mat d = EnsureGray(dark.img), eimg = EnsureGray(edge.img);
        int dist = ToInt(NodeParam(n, "expandDist", "12"), 12);
        cv::Mat cur = d.clone();
        cv::Mat ker = cv::getStructuringElement(cv::MORPH_ELLIPSE, cv::Size(3, 3));
        for (int i = 0; i < std::max(1, dist); ++i) {
            cv::Mat next;
            cv::dilate(cur, next, ker);
            next.setTo(255, eimg > 0);
            cur = next;
        }
        out["Out"] = MakeImage(cur);
        return true;
    }
    if (n.type == "filter_contours") {
        Value vc = InputOf(e, n.id, "Contours");
        if (vc.kind != Value::Kind::Contours) { err = "filter_contours: missing Contours"; return false; }
        auto contours = ToContours(vc.contours);
        double minArea = ToDouble(NodeParam(n, "minArea", "8000"), 8000);
        double maxArea = ToDouble(NodeParam(n, "maxArea", "4000000"), 4000000);
        int target = ToInt(NodeParam(n, "targetCount", "16"), 16);
        std::vector<std::vector<cv::Point>> kept;
        for (auto& c : contours) {
            double a = cv::contourArea(c);
            if (a < minArea || a > maxArea) continue;
            kept.push_back(c);
        }
        std::sort(kept.begin(), kept.end(), [](const auto& a, const auto& b) { return cv::contourArea(a) > cv::contourArea(b); });
        if ((int)kept.size() > target) kept.resize(target);
        out["Contours"] = MakeContours(kept);
        Value cnt; cnt.kind = Value::Kind::Int; cnt.i = (int)kept.size(); out["Count"] = cnt;
        return true;
    }
    if (n.type == "match_contours") {
        Value vc = InputOf(e, n.id, "Contours");
        if (vc.kind != Value::Kind::Contours) { err = "match_contours: missing Contours"; return false; }
        // 简化：先透传；后续可替换更精细的形状距离匹配
        out["Contours"] = vc;
        Value cnt; cnt.kind = Value::Kind::Int; cnt.i = vc.contours.count; out["Count"] = cnt;
        return true;
    }
    if (n.type == "fuse_contours_template") {
        Value vc = InputOf(e, n.id, "Contours");
        if (vc.kind != Value::Kind::Contours) { err = "fuse_contours_template: missing Contours"; return false; }
        auto contours = ToContours(vc.contours);
        if (contours.empty()) { err = "fuse_contours_template: empty contours"; return false; }
        int canvas = ToInt(NodeParam(n, "canvasSize", "120"), 120);
        canvas = std::max(32, canvas);
        cv::Rect bbox = cv::boundingRect(contours[0]);
        for (size_t i = 1; i < contours.size(); ++i) bbox |= cv::boundingRect(contours[i]);
        cv::Mat tpl = cv::Mat::zeros(std::max(canvas, bbox.height + 4), std::max(canvas, bbox.width + 4), CV_8UC1);
        for (auto& c : contours) {
            std::vector<cv::Point> t;
            t.reserve(c.size());
            for (auto& p : c) t.emplace_back(p.x - bbox.x + 2, p.y - bbox.y + 2);
            cv::drawContours(tpl, std::vector<std::vector<cv::Point>>{t}, 0, cv::Scalar(255), 1);
        }
        out["Template"] = MakeImage(tpl);
        return true;
    }
    if (n.type == "shape_match_global") {
        Value edge = InputOf(e, n.id, "Edge");
        if (edge.kind != Value::Kind::Image) { err = "shape_match_global: missing Edge"; return false; }
        cv::Mat edgeBin = EnsureGray(edge.img);
        Value tplIn = InputOf(e, n.id, "Template");
        cv::Mat tpl = tplIn.kind == Value::Kind::Image ? EnsureGray(tplIn.img) : cv::Mat();
        if (tpl.empty()) {
            Value vc = InputOf(e, n.id, "Contours");
            if (vc.kind == Value::Kind::Contours) {
                auto contours = ToContours(vc.contours);
                cv::Mat tmp = cv::Mat::zeros(edgeBin.size(), CV_8UC1);
                cv::drawContours(tmp, contours, 0, cv::Scalar(255), 1);
                cv::Rect b = cv::boundingRect(contours[0]);
                tpl = tmp(b).clone();
            } else {
                err = "shape_match_global: missing Template/Contours";
                return false;
            }
        }
        int maxSide = std::max(24, ToInt(NodeParam(n, "maxTemplateSize", "120"), 120));
        if (std::max(tpl.cols, tpl.rows) > maxSide) {
            double sc = (double)maxSide / std::max(tpl.cols, tpl.rows);
            cv::resize(tpl, tpl, cv::Size(), sc, sc, cv::INTER_NEAREST);
        }
        cv::Mat result;
        cv::matchTemplate(edgeBin, tpl, result, cv::TM_CCORR_NORMED);
        int topN = std::max(1, ToInt(NodeParam(n, "topN", "15"), 15));
        double minScore = ToDouble(NodeParam(n, "minScore", "0.18"), 0.18);
        std::vector<std::tuple<double, cv::Point>> cand;
        for (int y = 0; y < result.rows; ++y) {
            for (int x = 0; x < result.cols; ++x) {
                float s = result.at<float>(y, x);
                if (s >= minScore) cand.emplace_back((double)s, cv::Point(x, y));
            }
        }
        std::sort(cand.begin(), cand.end(), [](const auto& a, const auto& b) { return std::get<0>(a) > std::get<0>(b); });
        std::vector<std::vector<cv::Point>> outContours;
        cv::Mat tplPts;
        cv::findNonZero(tpl, tplPts);
        int keep = std::min(topN, (int)cand.size());
        for (int i = 0; i < keep; ++i) {
            auto p = std::get<1>(cand[i]);
            std::vector<cv::Point> c;
            c.reserve(tplPts.rows);
            for (int k = 0; k < tplPts.rows; ++k) {
                cv::Point tp = tplPts.at<cv::Point>(k);
                c.emplace_back(tp.x + p.x, tp.y + p.y);
            }
            outContours.push_back(std::move(c));
        }
        out["Contours"] = MakeContours(outContours);
        Value cnt; cnt.kind = Value::Kind::Int; cnt.i = (int)outContours.size(); out["Count"] = cnt;
        return true;
    }
    if (n.type == "sample") {
        Value vc = InputOf(e, n.id, "Contours");
        if (vc.kind != Value::Kind::Contours) { err = "sample: missing Contours"; return false; }
        int targetBars = ToInt(NodeParam(n, "targetBars", "16"), 16);
        double spacing = ToDouble(NodeParam(n, "spacing", "3"), 3.0);
        int maxPts = CALIB_MAX_TRAJ_POINTS;
        std::vector<Point2D> pts(maxPts);
        std::vector<int> bar(maxPts);
        int outCount = SampleContoursFromPoints(
            vc.contours.flatX.data(), vc.contours.flatY.data(),
            vc.contours.lengths.data(), vc.contours.count,
            targetBars, CALIB_IMAGE_WIDTH, CALIB_IMAGE_HEIGHT, spacing,
            pts.data(), bar.data(), maxPts);
        if (outCount < 0) { err = "sample: native sampling failed"; return false; }
        pts.resize(outCount);
        bar.resize(outCount);
        Value vPts; vPts.kind = Value::Kind::Points; vPts.points = pts; vPts.barIds = bar;
        out["Points"] = vPts;
        Value vBar; vBar.kind = Value::Kind::Points; vBar.barIds = bar; out["BarIds"] = vBar;
        return true;
    }
    if (n.type == "verify_mask") {
        Value v = InputOf(e, n.id, "In");
        Value m = InputOf(e, n.id, "Mask");
        if (v.kind != Value::Kind::Points || m.kind != Value::Kind::Image) { err = "verify_mask: missing input"; return false; }
        cv::Mat mask = EnsureGray(m.img);
        std::vector<Point2D> kept;
        std::vector<int> bar;
        kept.reserve(v.points.size());
        for (size_t i = 0; i < v.points.size(); ++i) {
            int x = (int)std::round(v.points[i].x), y = (int)std::round(v.points[i].y);
            if (x < 0 || y < 0 || x >= mask.cols || y >= mask.rows) continue;
            if (mask.at<unsigned char>(y, x) == 0) continue;
            kept.push_back(v.points[i]);
            if (i < v.barIds.size()) bar.push_back(v.barIds[i]);
        }
        Value ov; ov.kind = Value::Kind::Points; ov.points = std::move(kept); ov.barIds = std::move(bar);
        out["Out"] = ov;
        return true;
    }
    if (n.type == "dedup") {
        Value v = InputOf(e, n.id, "In");
        if (v.kind != Value::Kind::Points) { err = "dedup: missing In"; return false; }
        std::vector<cv::Point> pts;
        pts.reserve(v.points.size());
        for (auto& p : v.points) pts.emplace_back((int)std::round(p.x), (int)std::round(p.y));
        std::vector<int> bars = v.barIds;
        if (bars.size() != pts.size()) bars.assign(pts.size(), 0);
        Step_DeduplicateAndSort(&pts, &bars);
        Value ov; ov.kind = Value::Kind::Points;
        ov.points.reserve(pts.size());
        for (auto& p : pts) ov.points.push_back(Point2D{ (double)p.x, (double)p.y });
        ov.barIds = bars;
        out["Out"] = ov;
        return true;
    }
    if (n.type == "fit_shape") {
        Value v = InputOf(e, n.id, "In");
        Value vb = InputOf(e, n.id, "BarIds");
        if (v.kind != Value::Kind::Points) { err = "fit_shape: missing In"; return false; }
        std::vector<cv::Point> cvPts;
        cvPts.reserve(v.points.size());
        for (auto& p : v.points) cvPts.emplace_back((int)std::round(p.x), (int)std::round(p.y));
        std::vector<int> barIds = !vb.barIds.empty() ? vb.barIds : v.barIds;
        if (barIds.size() != cvPts.size()) barIds.assign(cvPts.size(), 0);
        int fitMode = 0;
        std::string mode = NodeParam(n, "mode", "hybrid");
        if (mode == "simplify") fitMode = 1;
        double eps = ToDouble(NodeParam(n, "epsilon", "2.0"), 2.0);
        Step_FitShape(&cvPts, &barIds, CALIB_IMAGE_WIDTH, CALIB_IMAGE_HEIGHT, fitMode, eps);
        Value ov; ov.kind = Value::Kind::Points;
        ov.points.reserve(cvPts.size());
        for (auto& p : cvPts) ov.points.push_back(Point2D{ (double)p.x, (double)p.y });
        ov.barIds = barIds;
        out["Out"] = ov;
        return true;
    }
    if (n.type == "detect_hollow") {
        Value in = InputOf(e, n.id, "Image");
        if (in.kind != Value::Kind::Image) in = InputOf(e, n.id, "Mask");
        if (in.kind != Value::Kind::Image) { err = "detect_hollow: missing input"; return false; }
        // 轻量实现：直接输出输入图，Count 置0（完整空洞流程使用 C++ TrajStep 可再扩展）
        out["Hollow"] = MakeImage(EnsureGray(in.img));
        Value c; c.kind = Value::Kind::Int; c.i = 0; out["Count"] = c;
        return true;
    }
    if (n.type == "sort_contours") {
        Value in = InputOf(e, n.id, "In");
        if (in.kind != Value::Kind::Image) { err = "sort_contours: missing In"; return false; }
        cv::Mat bin = EnsureGray(in.img);
        std::vector<std::vector<cv::Point>> contours;
        cv::findContours(bin, contours, cv::RETR_EXTERNAL, cv::CHAIN_APPROX_NONE);
        std::sort(contours.begin(), contours.end(), [](const auto& a, const auto& b) { return cv::contourArea(a) > cv::contourArea(b); });
        Value c; c.kind = Value::Kind::Int; c.i = (int)contours.size(); out["Count"] = c;
        return true;
    }
    if (n.type == "draw_color") {
        Value pts = InputOf(e, n.id, "In");
        if (pts.kind != Value::Kind::Points) { err = "draw_color: missing In"; return false; }
        cv::Mat img = cv::Mat::zeros(CALIB_IMAGE_HEIGHT, CALIB_IMAGE_WIDTH, CV_8UC3);
        for (auto& p : pts.points) cv::circle(img, cv::Point((int)p.x, (int)p.y), 1, cv::Scalar(0, 255, 0), -1);
        out["Out"] = MakeImage(img);
        return true;
    }
    if (n.type == "world_coords") {
        // Native 引擎无交互输入时返回空点集
        Value v; v.kind = Value::Kind::Points;
        out["Points"] = v;
        return true;
    }
    if (n.type == "calibrate") {
        Value v = InputOf(e, n.id, "ImagePts");
        Value w = InputOf(e, n.id, "WorldPts");
        if (v.kind != Value::Kind::Points || w.kind != Value::Kind::Points || v.points.size() < 3 || w.points.size() < 3) {
            err = "calibrate: missing points";
            return false;
        }
        int nPts = (int)std::min(v.points.size(), w.points.size());
        std::vector<Point2D> ip(nPts), wp(nPts);
        for (int i = 0; i < nPts; ++i) { ip[i] = v.points[i]; wp[i] = w.points[i]; }
        AffineTransform t{};
        if (CalibrateNinePoint(ip.data(), wp.data(), nPts, &t) != 0) { err = "calibrate failed"; return false; }
        Value tv; tv.kind = Value::Kind::Transform; tv.trans = t; out["Transform"] = tv;
        return true;
    }
    if (n.type == "img_to_world") {
        Value pts = InputOf(e, n.id, "Points");
        Value tr = InputOf(e, n.id, "Transform");
        if (pts.kind != Value::Kind::Points || tr.kind != Value::Kind::Transform) { err = "img_to_world: missing input"; return false; }
        Value outPts; outPts.kind = Value::Kind::Points;
        outPts.points.reserve(pts.points.size());
        for (auto& p : pts.points) outPts.points.push_back(ImageToWorld(p, tr.trans));
        out["World"] = outPts;
        return true;
    }
    if (n.type == "chessboard_find_corners") {
        Value vin = InputOf(e, n.id, "Image");
        if (vin.kind != Value::Kind::Image) { err = "chessboard_find_corners: missing Image"; return false; }
        int cols = ToInt(NodeParam(n, "cols", "9"), 9);
        int rows = ToInt(NodeParam(n, "rows", "6"), 6);
        int refine = ToInt(NodeParam(n, "refine", "1"), 1);
        int fast = ToInt(NodeParam(n, "fastCheck", "1"), 1);
        if (cols < 2 || rows < 2) { err = "chessboard_find_corners: cols/rows must be >=2"; return false; }
        cv::Mat gray = EnsureGray(vin.img);
        std::vector<unsigned char> buf((size_t)gray.cols * gray.rows);
        if (gray.isContinuous())
            memcpy(buf.data(), gray.ptr(), buf.size());
        else {
            for (int y = 0; y < gray.rows; ++y)
                memcpy(buf.data() + (size_t)y * gray.cols, gray.ptr(y), gray.cols);
        }
        int maxPts = cols * rows;
        std::vector<Point2D> pts((size_t)maxPts);
        int count = 0;
        FindChessboardCornersGrayBuffer(buf.data(), gray.cols, gray.rows, cols, rows, pts.data(), &count, maxPts, refine, fast);
        Value vpts; vpts.kind = Value::Kind::Points;
        if (count > 0) vpts.points.assign(pts.begin(), pts.begin() + count);
        out["Points"] = vpts;
        Value vf; vf.kind = Value::Kind::Int; vf.i = count > 0 ? 1 : 0; out["Found"] = vf;
        cv::Mat bgr = EnsureBgr(vin.img);
        std::vector<cv::Point2f> cvPts;
        cvPts.reserve((size_t)count);
        for (int i = 0; i < count; ++i) cvPts.emplace_back((float)vpts.points[(size_t)i].x, (float)vpts.points[(size_t)i].y);
        cv::drawChessboardCorners(bgr, cv::Size(cols, rows), cvPts, count == cols * rows);
        out["Vis"] = MakeImage(bgr);
        return true;
    }
    if (n.type == "chessboard_calibrate_intrinsics") {
        std::string paths = NodeParam(n, "imagePaths", "");
        int cols = ToInt(NodeParam(n, "cols", "9"), 9);
        int rows = ToInt(NodeParam(n, "rows", "6"), 6);
        double sq = ToDouble(NodeParam(n, "squareSizeMm", "25"), 25.0);
        double fx, fy, cx, cy, k1, k2, p1, p2, k3, rms;
        std::vector<char> jbuf(393216);
        int crc = CalibrateCameraChessboardMultiview(paths.c_str(), cols, rows, sq, &fx, &fy, &cx, &cy, &k1, &k2, &p1, &p2, &k3, &rms,
            jbuf.data(), (int)jbuf.size());
        if (crc != 0) { err = "chessboard_calibrate_intrinsics failed (need >=3 views with detected board)"; return false; }
        std::ostringstream j;
        j << std::fixed << std::setprecision(6);
        j << "{\"fx\":" << fx << ",\"fy\":" << fy << ",\"cx\":" << cx << ",\"cy\":" << cy
          << ",\"k1\":" << k1 << ",\"k2\":" << k2 << ",\"p1\":" << p1 << ",\"p2\":" << p2 << ",\"k3\":" << k3 << ",\"rms\":" << rms << "}";
        Value vs; vs.kind = Value::Kind::String; vs.str = j.str(); out["IntrinsicsJson"] = vs;
        Value vfull; vfull.kind = Value::Kind::String; vfull.str.assign(jbuf.data()); out["CalibrationJson"] = vfull;
        Value vd; vd.kind = Value::Kind::Double; vd.d = rms; out["Rms"] = vd;
        return true;
    }
    if (n.type == "chessboard_pixels_to_world") {
        Value ptsIn = InputOf(e, n.id, "Points");
        Value cal = InputOf(e, n.id, "CalibrationJson");
        if (ptsIn.kind != Value::Kind::Points) { err = "chessboard_pixels_to_world: missing Points"; return false; }
        if (cal.kind != Value::Kind::String || cal.str.empty()) { err = "chessboard_pixels_to_world: missing CalibrationJson"; return false; }
        if (ptsIn.points.empty()) { err = "chessboard_pixels_to_world: empty Points"; return false; }
        int viewIdx = ToInt(NodeParam(n, "viewIndex", "0"), 0);
        std::vector<Point2D> outw(ptsIn.points.size());
        int rc = PixelsToChessboardPlaneXYFromCalibrationJson(cal.str.c_str(), viewIdx,
            ptsIn.points.data(), outw.data(), (int)ptsIn.points.size());
        if (rc != 0) { err = "chessboard_pixels_to_world failed (code " + std::to_string(rc) + ")"; return false; }
        Value vout; vout.kind = Value::Kind::Points; vout.points = std::move(outw);
        out["World"] = vout;
        return true;
    }
    if (n.type == "points_to_text") {
        Value ptsIn = InputOf(e, n.id, "Points");
        if (ptsIn.kind != Value::Kind::Points) { err = "points_to_text: missing Points"; return false; }
        std::ostringstream sb;
        sb << std::setprecision(9);
        for (size_t i = 0; i < ptsIn.points.size(); ++i) {
            if (i) sb << "\n";
            sb << ptsIn.points[i].x << "," << ptsIn.points[i].y;
        }
        Value vt; vt.kind = Value::Kind::String; vt.str = sb.str();
        out["Text"] = vt;
        return true;
    }
    if (n.type == "detect_circles") {
        Value in = InputOf(e, n.id, "In");
        if (in.kind != Value::Kind::Image) { err = "detect_circles: missing In"; return false; }
        cv::Mat gray = EnsureGray(in.img);
        std::vector<cv::Vec3f> circles;
        cv::HoughCircles(gray, circles, cv::HOUGH_GRADIENT, 1.0, 20.0, 120, 20, 3, 60);
        Value pts; pts.kind = Value::Kind::Points;
        for (auto& c : circles) pts.points.push_back(Point2D{ c[0], c[1] });
        out["Points"] = pts;
        Value cnt; cnt.kind = Value::Kind::Int; cnt.i = (int)circles.size(); out["Count"] = cnt;
        return true;
    }
    if (n.type == "send_plc") {
        Value pts = InputOf(e, n.id, "Points");
        if (pts.kind != Value::Kind::Points) { err = "send_plc: missing Points"; return false; }
        Value cnt; cnt.kind = Value::Kind::Int; cnt.i = (int)pts.points.size(); out["Count"] = cnt;
        return true;
    }
    if (n.type == "composite") {
        // C++ 原生引擎暂不展开组合子图，先透传以保持流程可运行
        Value in = InputOf(e, n.id, "In");
        out["Out"] = in;
        return true;
    }
    if (n.type == "convert_output") {
        Value v = InputOf(e, n.id, "In");
        out["Out"] = v;
        return true;
    }
    if (n.type == "display") {
        // 原生引擎下 display 仅作为透传与占位
        Value img = InputOf(e, n.id, "Image");
        if (img.kind == Value::Kind::Image) out["Image"] = img;
        Value pts = InputOf(e, n.id, "Points");
        if (pts.kind == Value::Kind::Points) out["Points"] = pts;
        return true;
    }
    if (n.type == "save_image") {
        Value img = InputOf(e, n.id, "Image");
        if (img.kind != Value::Kind::Image) { err = "save_image: missing Image"; return false; }
        std::string path = NodeParam(n, "filePath", "flow_output.bmp");
        if (!cv::imwrite(path, img.img)) { err = "save_image: failed " + path; return false; }
        out["Out"] = img;
        return true;
    }
    if (n.type == "save_text") {
        Value v = InputOf(e, n.id, "Text");
        if (v.kind != Value::Kind::String) { err = "save_text: missing Text"; return false; }
        std::string path = NodeParam(n, "filePath", "flow_output.txt");
        std::ofstream ofs(path, std::ios::binary | std::ios::trunc);
        if (!ofs) { err = "save_text: cannot open " + path; return false; }
        ofs.write(v.str.data(), (std::streamsize)v.str.size());
        if (!ofs.good()) { err = "save_text: write failed " + path; return false; }
        Value ov; ov.kind = Value::Kind::String; ov.str = v.str; out["Out"] = ov;
        return true;
    }

    err = "unsupported operator: " + n.type;
    return false;
}

static bool LoadFlowFromCvFileStorage(NativeFlowEngineImpl* e, cv::FileStorage& fs, std::string& err) {
    e->nodes.clear();
    e->conns.clear();
    cv::FileNode nodes = fs["Nodes"];
    cv::FileNode conns = fs["Connections"];
    if (nodes.type() != cv::FileNode::SEQ) { err = "flow json missing Nodes"; return false; }
    if (conns.type() != cv::FileNode::SEQ) { err = "flow json missing Connections"; return false; }
    for (auto it = nodes.begin(); it != nodes.end(); ++it) {
        NodeDef nd;
        nd.id = (std::string)(*it)["Id"];
        nd.type = (std::string)(*it)["TypeId"];
        cv::FileNode params = (*it)["Params"];
        if (params.type() == cv::FileNode::MAP) {
            // OpenCV 4.13 的 FileNodeIterator 不提供 name()，改用 keys() 兼容写法
            std::vector<cv::String> keys = params.keys();
            for (const auto& k : keys) {
                nd.params[(std::string)k] = (std::string)params[k];
            }
        }
        e->nodes.push_back(std::move(nd));
    }
    for (auto it = conns.begin(); it != conns.end(); ++it) {
        ConnDef cd;
        cd.fromNodeId = (std::string)(*it)["FromNodeId"];
        cd.fromPort = (std::string)(*it)["FromPort"];
        cd.toNodeId = (std::string)(*it)["ToNodeId"];
        cd.toPort = (std::string)(*it)["ToPort"];
        e->conns.push_back(std::move(cd));
    }
    return true;
}

} // namespace

NativeFlowEngineHandle FlowEngine_Create() {
    return new NativeFlowEngineImpl();
}

void FlowEngine_Free(NativeFlowEngineHandle handle) {
    delete static_cast<NativeFlowEngineImpl*>(handle);
}

int FlowEngine_LoadFromFile(NativeFlowEngineHandle handle, const char* flowFilePath) {
    if (!handle || !flowFilePath) return -1;
    auto* e = static_cast<NativeFlowEngineImpl*>(handle);
    e->lastError.clear();
    cv::FileStorage fs(flowFilePath, cv::FileStorage::READ | cv::FileStorage::FORMAT_JSON);
    if (!fs.isOpened()) {
        e->lastError = std::string("cannot open flow file: ") + flowFilePath;
        return -1;
    }
    std::string err;
    bool ok = LoadFlowFromCvFileStorage(e, fs, err);
    fs.release();
    if (!ok) { e->lastError = err; return -1; }
    return 0;
}

int FlowEngine_LoadFromJson(NativeFlowEngineHandle handle, const char* flowJsonText) {
    if (!handle || !flowJsonText) return -1;
    auto* e = static_cast<NativeFlowEngineImpl*>(handle);
    e->lastError.clear();
    cv::FileStorage fs(std::string(flowJsonText), cv::FileStorage::READ | cv::FileStorage::FORMAT_JSON | cv::FileStorage::MEMORY);
    if (!fs.isOpened()) {
        e->lastError = "invalid flow json text";
        return -1;
    }
    std::string err;
    bool ok = LoadFlowFromCvFileStorage(e, fs, err);
    fs.release();
    if (!ok) { e->lastError = err; return -1; }
    return 0;
}

NativeFlowRunResult FlowEngine_Run(NativeFlowEngineHandle handle) {
    NativeFlowRunResult rr{ 0,0,0 };
    if (!handle) return rr;
    auto* e = static_cast<NativeFlowEngineImpl*>(handle);
    e->outputs.clear();
    e->nodeErrors.clear();
    e->lastError.clear();
    rr.totalNodes = (int)e->nodes.size();

    auto order = TopoSort(e);
    for (const auto& id : order) {
        const NodeDef* n = FindNode(e, id);
        if (!n) continue;
        std::string err;
        if (!ExecuteNode(e, *n, err)) {
            e->nodeErrors[id] = err;
            e->lastError = n->type + ": " + err;
            break;
        }
        rr.executedNodes++;
    }
    rr.success = rr.executedNodes == rr.totalNodes ? 1 : 0;

    std::ostringstream oss;
    oss << "{";
    oss << "\"success\":" << (rr.success ? "true" : "false") << ",";
    oss << "\"executedNodes\":" << rr.executedNodes << ",";
    oss << "\"totalNodes\":" << rr.totalNodes << ",";
    oss << "\"error\":\"";
    for (char ch : e->lastError) {
        if (ch == '\"') oss << "\\\"";
        else if (ch == '\\') oss << "\\\\";
        else if (ch == '\n') oss << "\\n";
        else oss << ch;
    }
    oss << "\"}";
    e->lastReportJson = oss.str();
    return rr;
}

const char* FlowEngine_GetLastError(NativeFlowEngineHandle handle) {
    if (!handle) return "";
    return static_cast<NativeFlowEngineImpl*>(handle)->lastError.c_str();
}

const char* FlowEngine_GetLastReportJson(NativeFlowEngineHandle handle) {
    if (!handle) return "{}";
    return static_cast<NativeFlowEngineImpl*>(handle)->lastReportJson.c_str();
}
