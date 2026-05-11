#include "FlowEngineNative.h"
#include <algorithm>
#include <chrono>
#include <cctype>
#include <cmath>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <fstream>
#include <opencv2/opencv.hpp>
#include <unordered_map>
#include <unordered_set>
#include <queue>
#include <sstream>
#include <iomanip>
#include <vector>

#ifdef _WIN32
#ifndef WIN32_LEAN_AND_MEAN
#define WIN32_LEAN_AND_MEAN
#endif
#ifndef NOMINMAX
#define NOMINMAX
#endif
#include <Windows.h>
#endif

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
    /// 主流程所在目录（LoadFromFile / LoadFromJson 设置）；解析相对 innerFlowPath、标定路径等。
    std::string flowRootDir;
};

static bool FlowPathIsAbsolute(const std::string& p) {
    if (p.empty()) return false;
    if (p.size() >= 2 && (unsigned char)p[1] == ':') return true;
    return p[0] == '/' || p[0] == '\\';
}

static std::string FlowDirName(const std::string& path) {
    size_t pos = path.find_last_of("\\/");
    if (pos == std::string::npos) return std::string();
    return path.substr(0, pos);
}

static std::string FlowJoinPath(const std::string& base, const std::string& rel) {
    if (base.empty()) return rel;
    char last = base.back();
    if (last == '\\' || last == '/') return base + rel;
    return base + "\\" + rel;
}

#ifdef _WIN32
static std::string FlowCanonicalPathA(const std::string& p) {
    if (p.empty()) return p;
    std::vector<char> buf(65536);
    DWORD n = GetFullPathNameA(p.c_str(), (DWORD)buf.size(), buf.data(), nullptr);
    if (n == 0 || n >= buf.size()) return p;
    return std::string(buf.data(), n);
}
#else
static std::string FlowCanonicalPathA(const std::string& p) {
    return p;
}
#endif

static std::string NodeParam(const NodeDef& node, const char* key, const char* defVal = "") {
    auto it = node.params.find(key);
    if (it == node.params.end()) return defVal ? defVal : "";
    return it->second;
}

static std::string TrimFlowToken(std::string s) {
    while (!s.empty() && (unsigned char)s.front() <= 32)
        s.erase(s.begin());
    while (!s.empty() && (unsigned char)s.back() <= 32)
        s.pop_back();
    return s;
}

static bool PathLessInsensitive(const std::string& a, const std::string& b) {
#if defined(_WIN32)
    return _stricmp(a.c_str(), b.c_str()) < 0;
#else
    return a < b;
#endif
}

static bool PathEqInsensitive(const std::string& a, const std::string& b) {
#if defined(_WIN32)
    return _stricmp(a.c_str(), b.c_str()) == 0;
#else
    return a == b;
#endif
}

/// 二进制读入标定 JSON；去掉 UTF-8 BOM / 前导空白，避免 OpenCV FileStorage 报 “left-brace of top level is missing”。
static bool ReadCalibrationJsonFileForOpenCv(const std::string& path, std::string& utf8Json, std::string& err) {
    std::ifstream ifs(path, std::ios::binary);
    if (!ifs) {
        err = "cannot read file: " + path;
        return false;
    }
    std::ostringstream oss;
    oss << ifs.rdbuf();
    utf8Json = oss.str();
    if (utf8Json.size() >= 3 && (unsigned char)utf8Json[0] == 0xEF && (unsigned char)utf8Json[1] == 0xBB &&
        (unsigned char)utf8Json[2] == 0xBF)
        utf8Json.erase(0, 3);
    if (utf8Json.size() >= 2 && (unsigned char)utf8Json[0] == 0xFF && (unsigned char)utf8Json[1] == 0xFE) {
        err = "calibration JSON is UTF-16 LE; save as UTF-8 (preferably without BOM)";
        return false;
    }
    if (utf8Json.size() >= 2 && (unsigned char)utf8Json[0] == 0xFE && (unsigned char)utf8Json[1] == 0xFF) {
        err = "calibration JSON is UTF-16 BE; save as UTF-8 (preferably without BOM)";
        return false;
    }
    size_t start = 0;
    while (start < utf8Json.size()) {
        unsigned char c = (unsigned char)utf8Json[start];
        if (c == ' ' || c == '\t' || c == '\r' || c == '\n')
            ++start;
        else
            break;
    }
    if (start > 0)
        utf8Json.erase(0, start);
    if (utf8Json.empty() || utf8Json.front() != '{') {
        err = "calibration JSON must be UTF-8 object starting with '{' (remove BOM / fix encoding)";
        return false;
    }
    return true;
}

/// 从标定结果 JSON 中解析 affine（不经过 OpenCV FileStorage，避免其 JSON 方言/异常与 BOM 问题）。
static bool FindAffineObjectBounds(const std::string& json, size_t& innerBegin, size_t& innerEnd, std::string& err) {
    size_t keyPos = json.find("\"affine\"");
    if (keyPos == std::string::npos)
        keyPos = json.find("\"Affine\"");
    if (keyPos == std::string::npos) {
        err = "missing \"affine\" object";
        return false;
    }
    size_t i = keyPos;
    while (i < json.size() && json[i] != ':')
        ++i;
    if (i >= json.size()) {
        err = "affine: missing ':'";
        return false;
    }
    ++i;
    while (i < json.size() && std::isspace((unsigned char)json[i]))
        ++i;
    if (i >= json.size() || json[i] != '{') {
        err = "affine: expected '{'";
        return false;
    }
    const size_t startBrace = i;
    int depth = 0;
    bool inStr = false;
    bool esc = false;
    for (; i < json.size(); ++i) {
        const char c = json[i];
        if (inStr) {
            if (esc) {
                esc = false;
                continue;
            }
            if (c == '\\') {
                esc = true;
                continue;
            }
            if (c == '"')
                inStr = false;
            continue;
        }
        if (c == '"') {
            inStr = true;
            continue;
        }
        if (c == '{')
            ++depth;
        else if (c == '}') {
            --depth;
            if (depth == 0) {
                innerBegin = startBrace + 1;
                innerEnd = i;
                return true;
            }
        }
    }
    err = "affine: unclosed '}'";
    return false;
}

static bool ParseAffineSixDoubles(const std::string& slice, AffineTransform& t, std::string& err) {
    const char* names[] = { "a", "b", "c", "d", "e", "f" };
    double* ptrs[] = { &t.a, &t.b, &t.c, &t.d, &t.e, &t.f };
    for (int k = 0; k < 6; ++k) {
        const std::string q = std::string("\"") + names[k] + "\"";
        size_t p = slice.find(q);
        if (p == std::string::npos) {
            err = std::string("missing affine field \"") + names[k] + "\"";
            return false;
        }
        p = slice.find(':', p);
        if (p == std::string::npos) {
            err = "affine: malformed ':'";
            return false;
        }
        ++p;
        while (p < slice.size() && std::isspace((unsigned char)slice[p]))
            ++p;
        char* endPtr = nullptr;
        const char* base = slice.c_str() + p;
        double v = std::strtod(base, &endPtr);
        if (endPtr == base) {
            err = std::string("affine: invalid number for \"") + names[k] + "\"";
            return false;
        }
        *ptrs[k] = v;
    }
    return true;
}

static bool ParseCalibrationResultJsonMinimal(const std::string& json, AffineTransform& t, std::string* calibrationJsonOut, std::string& err) {
    size_t innerBegin = 0, innerEnd = 0;
    if (!FindAffineObjectBounds(json, innerBegin, innerEnd, err))
        return false;
    if (innerEnd <= innerBegin) {
        err = "affine: empty object";
        return false;
    }
    std::string slice = json.substr(innerBegin, innerEnd - innerBegin);
    if (!ParseAffineSixDoubles(slice, t, err))
        return false;

    if (calibrationJsonOut) {
        calibrationJsonOut->clear();
        size_t k = json.find("\"calibrationJson\"");
        if (k == std::string::npos)
            k = json.find("\"CalibrationJson\"");
        if (k != std::string::npos) {
            size_t i = json.find(':', k);
            if (i != std::string::npos) {
                ++i;
                while (i < json.size() && std::isspace((unsigned char)json[i]))
                    ++i;
                if (i < json.size() && json[i] == '"') {
                    ++i;
                    std::string acc;
                    bool esc2 = false;
                    for (; i < json.size(); ++i) {
                        char c = json[i];
                        if (esc2) {
                            if (c == 'n')
                                acc.push_back('\n');
                            else if (c == 'r')
                                acc.push_back('\r');
                            else if (c == 't')
                                acc.push_back('\t');
                            else
                                acc.push_back(c);
                            esc2 = false;
                            continue;
                        }
                        if (c == '\\') {
                            esc2 = true;
                            continue;
                        }
                        if (c == '"')
                            break;
                        acc.push_back(c);
                    }
                    *calibrationJsonOut = std::move(acc);
                }
            }
        }
    }
    return true;
}

static int ToInt(const std::string& s, int defVal) {
    try { return std::stoi(s); } catch (...) { return defVal; }
}

static double ToDouble(const std::string& s, double defVal) {
    try { return std::stod(s); } catch (...) { return defVal; }
}

// Polyline helpers aligned with FlowPage.xaml.cs (SimplifyOpenPolyline / closed / resample / smooth / splits).

static double FlowPtLineDist(Point2D p, Point2D a, Point2D b) {
    double vx = b.x - a.x;
    double vy = b.y - a.y;
    double wx = p.x - a.x;
    double wy = p.y - a.y;
    double c1 = vx * wx + vy * wy;
    if (c1 <= 0) return std::hypot(wx, wy);
    double c2 = vx * vx + vy * vy;
    if (c2 <= 1e-9) return std::hypot(p.x - a.x, p.y - a.y);
    double t = c1 / c2;
    double px = a.x + t * vx;
    double py = a.y + t * vy;
    return std::hypot(p.x - px, p.y - py);
}

static std::vector<Point2D> FlowSimplifyOpenPolyline(const std::vector<Point2D>& points, double epsilon) {
    if (points.size() <= 2) return points;
    int index = -1;
    double maxDist = -1;
    Point2D start = points.front();
    Point2D end = points.back();
    for (size_t i = 1; i + 1 < points.size(); ++i) {
        double dist = FlowPtLineDist(points[i], start, end);
        if (dist > maxDist) {
            maxDist = dist;
            index = (int)i;
        }
    }
    if (maxDist <= epsilon || index <= 0) return { start, end };
    std::vector<Point2D> left(points.begin(), points.begin() + index + 1);
    std::vector<Point2D> right(points.begin() + index, points.end());
    auto L = FlowSimplifyOpenPolyline(left, epsilon);
    auto R = FlowSimplifyOpenPolyline(right, epsilon);
    if (!L.empty()) L.pop_back();
    L.insert(L.end(), R.begin(), R.end());
    return L;
}

static std::vector<Point2D> FlowSimplifyClosedPolyline(const std::vector<Point2D>& pts, double epsilon) {
    if (pts.size() < 4 || epsilon <= 0) return pts;
    std::vector<Point2D> open = pts;
    open.push_back(pts[0]);
    auto simplified = FlowSimplifyOpenPolyline(open, epsilon);
    if (simplified.size() > 1) simplified.pop_back();
    return simplified;
}

static std::vector<Point2D> FlowResampleClosedPolyline(const std::vector<Point2D>& points, int targetCount) {
    if (points.empty() || targetCount <= 0) return {};
    if (points.size() == 1) return std::vector<Point2D>((size_t)targetCount, points[0]);
    int n = (int)points.size();
    std::vector<double> cum((size_t)n + 1);
    cum[0] = 0;
    for (int i = 0; i < n; ++i) {
        double dx = points[(size_t)((i + 1) % n)].x - points[(size_t)i].x;
        double dy = points[(size_t)((i + 1) % n)].y - points[(size_t)i].y;
        cum[(size_t)i + 1] = cum[(size_t)i] + std::hypot(dx, dy);
    }
    double perimeter = cum[(size_t)n];
    if (perimeter <= 1e-6) return std::vector<Point2D>((size_t)targetCount, points[0]);
    std::vector<Point2D> result((size_t)targetCount);
    for (int i = 0; i < targetCount; ++i) {
        double s = (i * perimeter) / targetCount;
        int seg = 0;
        while (seg < n - 1 && cum[(size_t)seg + 1] < s) seg++;
        double segStart = cum[(size_t)seg];
        double segLen = cum[(size_t)seg + 1] - segStart;
        Point2D a = points[(size_t)seg];
        Point2D b = points[(size_t)((seg + 1) % n)];
        double t = segLen <= 1e-9 ? 0 : (s - segStart) / segLen;
        result[(size_t)i].x = a.x + (b.x - a.x) * t;
        result[(size_t)i].y = a.y + (b.y - a.y) * t;
    }
    return result;
}

static std::vector<Point2D> FlowSmoothPointsClosed(const std::vector<Point2D>& points, int windowRadius) {
    if (points.size() < 3 || windowRadius <= 0) return points;
    int n = (int)points.size();
    std::vector<Point2D> smoothed((size_t)n);
    for (int i = 0; i < n; ++i) {
        double sx = 0, sy = 0;
        int cnt = 0;
        for (int k = -windowRadius; k <= windowRadius; ++k) {
            int idx = i + k;
            while (idx < 0) idx += n;
            while (idx >= n) idx -= n;
            sx += points[(size_t)idx].x;
            sy += points[(size_t)idx].y;
            cnt++;
        }
        smoothed[(size_t)i].x = sx / cnt;
        smoothed[(size_t)i].y = sy / cnt;
    }
    return smoothed;
}

static std::vector<std::vector<Point2D>> FlowSplitIntoClosedRegions(const std::vector<Point2D>& points, double splitGapFactor, int minRegionPoints) {
    std::vector<std::vector<Point2D>> regions;
    if (points.empty()) return regions;
    int minR = std::max(3, minRegionPoints);
    if (points.size() < 4) {
        regions.push_back(points);
        return regions;
    }
    std::vector<double> steps;
    steps.reserve(points.size() - 1);
    for (size_t i = 0; i + 1 < points.size(); ++i)
        steps.push_back(std::hypot(points[i + 1].x - points[i].x, points[i + 1].y - points[i].y));
    std::vector<double> ordered = steps;
    std::sort(ordered.begin(), ordered.end());
    double medianStep = ordered.empty() ? 0 : ordered[ordered.size() / 2];
    if (medianStep <= 1e-9 || splitGapFactor <= 1.0) {
        regions.push_back(points);
        return regions;
    }
    double threshold = medianStep * splitGapFactor;
    size_t start = 0;
    for (size_t i = 0; i < steps.size(); ++i) {
        if (steps[i] <= threshold) continue;
        int len = (int)(i - start + 1);
        if (len >= minR)
            regions.emplace_back(points.begin() + (std::ptrdiff_t)start, points.begin() + (std::ptrdiff_t)(i + 1));
        start = i + 1;
    }
    int tailLen = (int)(points.size() - start);
    if (tailLen >= minR)
        regions.emplace_back(points.begin() + (std::ptrdiff_t)start, points.end());
    if (regions.empty())
        regions.push_back(points);
    return regions;
}

static std::vector<std::pair<std::vector<Point2D>, std::vector<int>>> FlowSplitRegionsByBarIds(
    const std::vector<Point2D>& points, const std::vector<int>& barIds, int minRegionPoints) {
    std::vector<std::pair<std::vector<Point2D>, std::vector<int>>> regions;
    if (points.empty() || barIds.size() != points.size()) return regions;
    int minR = std::max(3, minRegionPoints);
    size_t start = 0;
    for (size_t i = 1; i < barIds.size(); ++i) {
        if (barIds[i] == barIds[i - 1]) continue;
        size_t len = i - start;
        if ((int)len >= minR) {
            regions.emplace_back(
                std::vector<Point2D>(points.begin() + (std::ptrdiff_t)start, points.begin() + (std::ptrdiff_t)i),
                std::vector<int>(barIds.begin() + (std::ptrdiff_t)start, barIds.begin() + (std::ptrdiff_t)i));
        }
        start = i;
    }
    size_t tailLen = barIds.size() - start;
    if ((int)tailLen >= minR) {
        regions.emplace_back(
            std::vector<Point2D>(points.begin() + (std::ptrdiff_t)start, points.end()),
            std::vector<int>(barIds.begin() + (std::ptrdiff_t)start, barIds.end()));
    }
    return regions;
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

struct LinesJsonBinKey {
    int tb = 0;
    int rb = 0;
    bool operator==(const LinesJsonBinKey& o) const { return tb == o.tb && rb == o.rb; }
};
struct LinesJsonBinKeyHash {
    size_t operator()(const LinesJsonBinKey& k) const {
        return (static_cast<size_t>(k.tb) * 1315423911u) ^ (static_cast<size_t>(k.rb) * 2654435761u);
    }
};

static std::string FormatLinesJsonVec4(const std::vector<cv::Vec4i>& segs) {
    std::ostringstream jb;
    jb << '[';
    for (size_t i = 0; i < segs.size(); ++i) {
        const auto& s = segs[i];
        if (i) jb << ',';
        jb << '[' << s[0] << ',' << s[1] << ',' << s[2] << ',' << s[3] << ']';
    }
    jb << ']';
    return jb.str();
}

static void LinesJsonNmsBucket(const std::vector<cv::Vec4i>& in, double angleTolDeg, double rhoTolPx, std::vector<cv::Vec4i>& out) {
    out.clear();
    if (in.empty()) return;
    double angleTolRad = angleTolDeg * CV_PI / 180.0;
    if (angleTolRad < 1e-9) angleTolRad = 1e-9;
    if (rhoTolPx < 1e-9) rhoTolPx = 1e-9;
    std::unordered_map<LinesJsonBinKey, cv::Vec4i, LinesJsonBinKeyHash> best;
    best.reserve(in.size());
    for (const auto& seg : in) {
        double dx = (double)(seg[2] - seg[0]), dy = (double)(seg[3] - seg[1]);
        double len = std::hypot(dx, dy);
        if (len < 1e-6) continue;
        double thetaLine = std::atan2(dy, dx);
        double thetaN = thetaLine + CV_PI / 2;
        while (thetaN < 0) thetaN += CV_PI;
        while (thetaN >= CV_PI) thetaN -= CV_PI;
        double mx = 0.5 * (seg[0] + seg[2]), my = 0.5 * (seg[1] + seg[3]);
        double rho = mx * std::cos(thetaN) + my * std::sin(thetaN);
        LinesJsonBinKey k{ (int)std::floor(thetaN / angleTolRad), (int)std::floor(rho / rhoTolPx) };
        auto it = best.find(k);
        if (it == best.end()) {
            best.emplace(k, seg);
        } else {
            double ox = (double)(it->second[2] - it->second[0]), oy = (double)(it->second[3] - it->second[1]);
            if (len > std::hypot(ox, oy))
                it->second = seg;
        }
    }
    out.reserve(best.size());
    for (const auto& kv : best)
        out.push_back(kv.second);
}

static void GrayMorphRectGrayIter(cv::Mat& gray, bool dilate, int kw, int kh, int iterations) {
    kw = std::max(1, kw | 1);
    kh = std::max(1, kh | 1);
    iterations = std::max(1, iterations);
    cv::Mat ker = cv::getStructuringElement(cv::MORPH_RECT, cv::Size(kw, kh));
    for (int i = 0; i < iterations; ++i) {
        cv::Mat next;
        if (dilate)
            cv::dilate(gray, next, ker);
        else
            cv::erode(gray, next, ker);
        gray = next;
    }
}

static bool GrayRangeBinaryPercentile(const cv::Mat& grayIn, double excludeLowPct, double excludeHighPct, cv::Mat& binOut, std::string& err) {
    if (grayIn.empty()) {
        err = "gray_range_binary: empty image";
        return false;
    }
    excludeLowPct = std::max(0.0, std::min(100.0, excludeLowPct));
    excludeHighPct = std::max(0.0, std::min(100.0, excludeHighPct));
    if (excludeLowPct + excludeHighPct >= 100.0) {
        err = "gray_range_binary: percentile excludes must sum to < 100%";
        return false;
    }
    cv::Mat gray = EnsureGray(grayIn);
    int count = gray.rows * gray.cols;
    int hist[256]{};
    const uchar* p = gray.ptr<uchar>();
    for (int i = 0; i < count; ++i)
        hist[p[i]]++;

    double total = (double)count;
    double lowMass = excludeLowPct * 0.01 * total;
    double highMass = (100.0 - excludeHighPct) * 0.01 * total;

    int usedLow = 255;
    int cum = 0;
    for (int i = 0; i < 256; ++i) {
        cum += hist[i];
        if (cum >= lowMass - 1e-9) {
            usedLow = i;
            break;
        }
    }
    int usedHigh = 0;
    cum = 0;
    for (int i = 0; i < 256; ++i) {
        cum += hist[i];
        if (cum >= highMass - 1e-9) {
            usedHigh = i;
            break;
        }
    }
    if (usedLow > usedHigh)
        std::swap(usedLow, usedHigh);

    binOut = cv::Mat::zeros(gray.size(), CV_8UC1);
    uchar* d = binOut.ptr<uchar>();
    for (int i = 0; i < count; ++i) {
        uchar g = p[i];
        d[i] = (g >= usedLow && g <= usedHigh) ? (uchar)255 : (uchar)0;
    }
    return true;
}

static bool ReadFlowGraph(cv::FileStorage& fs, std::vector<NodeDef>& nodes, std::vector<ConnDef>& conns, std::string& err) {
    nodes.clear();
    conns.clear();
    cv::FileNode fnNodes = fs["Nodes"];
    cv::FileNode fnConns = fs["Connections"];
    if (fnNodes.type() != cv::FileNode::SEQ) {
        err = "flow json missing Nodes";
        return false;
    }
    if (fnConns.type() != cv::FileNode::SEQ) {
        err = "flow json missing Connections";
        return false;
    }
    for (auto it = fnNodes.begin(); it != fnNodes.end(); ++it) {
        NodeDef nd;
        nd.id = TrimFlowToken((std::string)(*it)["Id"]);
        nd.type = TrimFlowToken((std::string)(*it)["TypeId"]);
        cv::FileNode params = (*it)["Params"];
        if (params.type() == cv::FileNode::MAP) {
            std::vector<cv::String> keys = params.keys();
            for (const auto& k : keys)
                nd.params[(std::string)k] = (std::string)params[k];
        }
        nodes.push_back(std::move(nd));
    }
    for (auto it = fnConns.begin(); it != fnConns.end(); ++it) {
        ConnDef cd;
        cd.fromNodeId = (std::string)(*it)["FromNodeId"];
        cd.fromPort = (std::string)(*it)["FromPort"];
        cd.toNodeId = (std::string)(*it)["ToNodeId"];
        cd.toPort = (std::string)(*it)["ToPort"];
        auto tit = std::find_if(nodes.begin(), nodes.end(), [&](const NodeDef& nd) { return nd.id == cd.toNodeId; });
        if (tit != nodes.end() && tit->type == "hough_lines" && cd.toPort == "Image")
            cd.toPort = "Edge";
        conns.push_back(std::move(cd));
    }
    return true;
}

static bool ExpandOneComposite(NativeFlowEngineImpl* e, size_t compositeIdx, std::string& err) {
    if (compositeIdx >= e->nodes.size()) {
        err = "composite: invalid index";
        return false;
    }
    const NodeDef& comp = e->nodes[compositeIdx];
    std::string path = NodeParam(comp, "innerFlowPath", "");
    std::string embedded = NodeParam(comp, "innerFlowJson", "");

    cv::FileStorage fs;
    if (!path.empty()) {
        std::string openPath = path;
        if (!FlowPathIsAbsolute(path) && !e->flowRootDir.empty())
            openPath = FlowJoinPath(e->flowRootDir, path);
        fs.open(openPath, cv::FileStorage::READ | cv::FileStorage::FORMAT_JSON);
        if (!fs.isOpened()) {
            err = "composite: cannot open innerFlowPath: " + path;
            return false;
        }
    } else if (!embedded.empty()) {
        if (!fs.open(embedded, cv::FileStorage::READ | cv::FileStorage::FORMAT_JSON | cv::FileStorage::MEMORY)) {
            err = "composite: innerFlowJson is not valid JSON";
            return false;
        }
    } else {
        err = "composite: need innerFlowPath or innerFlowJson";
        return false;
    }

    std::vector<NodeDef> innerNodes;
    std::vector<ConnDef> innerConns;
    if (!ReadFlowGraph(fs, innerNodes, innerConns, err)) {
        fs.release();
        return false;
    }
    fs.release();

    std::unordered_map<std::string, std::pair<std::string, std::string>> bindInMap;
    std::unordered_map<std::string, std::pair<std::string, std::string>> bindOutMap;
    std::unordered_set<std::string> bindIds;

    for (const auto& inn : innerNodes) {
        if (inn.type == "composite_bind_in") {
            bindIds.insert(inn.id);
            std::string ext = NodeParam(inn, "externalPort", "In");
            for (const auto& ic : innerConns) {
                if (ic.fromNodeId == inn.id && ic.fromPort == "Out") {
                    bindInMap[ext] = { ic.toNodeId, ic.toPort };
                    break;
                }
            }
        } else if (inn.type == "composite_bind_out") {
            bindIds.insert(inn.id);
            std::string ext = NodeParam(inn, "externalPort", "Out");
            for (const auto& ic : innerConns) {
                if (ic.toNodeId == inn.id && ic.toPort == "In") {
                    bindOutMap[ext] = { ic.fromNodeId, ic.fromPort };
                    break;
                }
            }
        }
    }

    const std::string prefix = comp.id + "__";

    std::vector<NodeDef> newNodes;
    std::vector<ConnDef> newConns;
    newNodes.reserve(e->nodes.size() + innerNodes.size());
    newConns.reserve(e->conns.size() + innerConns.size() + 8);

    for (size_t i = 0; i < e->nodes.size(); ++i) {
        if (i == compositeIdx)
            continue;
        newNodes.push_back(e->nodes[i]);
    }

    for (const auto& inn : innerNodes) {
        if (bindIds.count(inn.id))
            continue;
        NodeDef nn = inn;
        nn.id = prefix + inn.id;
        newNodes.push_back(std::move(nn));
    }

    for (const auto& c : e->conns) {
        if (c.fromNodeId == comp.id || c.toNodeId == comp.id)
            continue;
        newConns.push_back(c);
    }

    for (const auto& ic : innerConns) {
        if (bindIds.count(ic.fromNodeId) || bindIds.count(ic.toNodeId))
            continue;
        ConnDef nc = ic;
        nc.fromNodeId = prefix + ic.fromNodeId;
        nc.toNodeId = prefix + ic.toNodeId;
        newConns.push_back(std::move(nc));
    }

    for (const auto& c : e->conns) {
        if (c.toNodeId != comp.id)
            continue;
        auto it = bindInMap.find(c.toPort);
        if (it == bindInMap.end()) {
            err = "composite: no composite_bind_in for external input port '" + c.toPort + "'";
            return false;
        }
        ConnDef nc;
        nc.fromNodeId = c.fromNodeId;
        nc.fromPort = c.fromPort;
        nc.toNodeId = prefix + it->second.first;
        nc.toPort = it->second.second;
        newConns.push_back(std::move(nc));
    }

    for (const auto& c : e->conns) {
        if (c.fromNodeId != comp.id)
            continue;
        auto it = bindOutMap.find(c.fromPort);
        if (it == bindOutMap.end()) {
            err = "composite: no composite_bind_out for external output port '" + c.fromPort + "'";
            return false;
        }
        ConnDef nc;
        nc.fromNodeId = prefix + it->second.first;
        nc.fromPort = it->second.second;
        nc.toNodeId = c.toNodeId;
        nc.toPort = c.toPort;
        newConns.push_back(std::move(nc));
    }

    e->nodes = std::move(newNodes);
    e->conns = std::move(newConns);
    return true;
}

static bool ExpandAllCompositeNodes(NativeFlowEngineImpl* e, std::string& err) {
    for (;;) {
        size_t idx = SIZE_MAX;
        for (size_t i = 0; i < e->nodes.size(); ++i) {
            if (e->nodes[i].type == "composite") {
                idx = i;
                break;
            }
        }
        if (idx == SIZE_MAX)
            return true;
        if (!ExpandOneComposite(e, idx, err))
            return false;
    }
}

static void LinesJsonThresholdLen(const std::vector<cv::Vec4i>& in, double minLen, double maxLen, std::vector<cv::Vec4i>& out) {
    out.clear();
    const double maxL = maxLen <= 0 ? 1e300 : maxLen;
    for (const auto& s : in) {
        double len = std::hypot((double)(s[2] - s[0]), (double)(s[3] - s[1]));
        if (len >= minLen && len <= maxL)
            out.push_back(s);
    }
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

/// 从 rootId 沿连线正向可达的节点集合（含自身）。
static std::unordered_set<std::string> DownstreamIdsFrom(const NativeFlowEngineImpl* e, const std::string& rootId) {
    std::unordered_set<std::string> seen;
    std::queue<std::string> q;
    seen.insert(rootId);
    q.push(rootId);
    while (!q.empty()) {
        std::string id = q.front();
        q.pop();
        for (const auto& c : e->conns) {
            if (c.fromNodeId != id) continue;
            if (seen.insert(c.toNodeId).second)
                q.push(c.toNodeId);
        }
    }
    return seen;
}

static std::string GlobSuffixFromToken(std::string tok) {
    tok = TrimFlowToken(tok);
    if (tok.empty()) return "";
    if (tok.size() >= 2 && tok[0] == '*' && tok[1] == '.') return tok;
    if (!tok.empty() && tok[0] == '.') return "*" + tok;
    return "*." + tok;
}

static void CollectImagePathsFromDir(const std::string& dir, const std::string& extSpec, std::vector<std::string>& paths) {
    paths.clear();
    std::stringstream ss(extSpec);
    std::string rawTok;
    while (std::getline(ss, rawTok, ';')) {
        std::string suf = GlobSuffixFromToken(rawTok);
        if (suf.empty()) continue;
        std::string globPat = dir;
        if (!globPat.empty()) {
            char last = globPat.back();
            if (last != '\\' && last != '/') globPat += '\\';
        }
        globPat += suf;
        std::vector<cv::String> cvpaths;
        cv::glob(globPat, cvpaths, false);
        for (const auto& p : cvpaths) paths.emplace_back(std::string(p));
    }
    std::sort(paths.begin(), paths.end(), PathLessInsensitive);
    paths.erase(std::unique(paths.begin(), paths.end(), PathEqInsensitive), paths.end());
}

static bool FillLoadImageDirOutputsForPath(NativeFlowEngineImpl* e, const NodeDef& n, const std::string& path, int totalCount,
    std::string& err) {
    cv::Mat img = cv::imread(path, cv::IMREAD_UNCHANGED);
    if (img.empty()) {
        err = "failed to read " + path;
        return false;
    }
    auto& out = e->outputs[n.id];
    out.clear();
    out["Image"] = MakeImage(img.channels() == 1 ? img : EnsureBgr(img));
    Value cnt;
    cnt.kind = Value::Kind::Int;
    cnt.i = totalCount;
    out["Count"] = cnt;
    Value vp;
    vp.kind = Value::Kind::String;
    vp.str = path;
    out["Path"] = vp;
    return true;
}

static bool HasCameraLoopPerFrame(const NativeFlowEngineImpl* e) {
    for (const auto& n : e->nodes) {
        if (n.type != "camera_loop") continue;
        std::string m = TrimFlowToken(NodeParam(n, "mode", "last_only"));
        for (char& ch : m) ch = (char)std::tolower((unsigned char)ch);
        if (m == "per_frame") return true;
    }
    return false;
}

/// 解析唯一的 load_image_dir(mode=each)；多个 each 返回 false 并写 err。
static bool ResolveLoadImageDirEachLoopId(const NativeFlowEngineImpl* e, std::string& outLoopId, std::string& err) {
    outLoopId.clear();
    err.clear();
    for (const auto& n : e->nodes) {
        if (n.type != "load_image_dir") continue;
        std::string mode = TrimFlowToken(NodeParam(n, "mode", "each"));
        for (char& ch : mode) ch = (char)std::tolower((unsigned char)ch);
        if (mode != "each") continue;
        if (!outLoopId.empty()) {
            err = "load_image_dir: multiple mode=each not supported";
            return false;
        }
        outLoopId = n.id;
    }
    return true;
}

static const NodeDef* FindNode(const NativeFlowEngineImpl* e, const std::string& id) {
    for (const auto& n : e->nodes) if (n.id == id) return &n;
    return nullptr;
}

static bool ExecuteNode(NativeFlowEngineImpl* e, const NodeDef& n, std::string& err);

static bool RunLoadImageDirEach(NativeFlowEngineImpl* e, const std::string& loopId, NativeFlowRunResult& rr) {
    const NodeDef* loopNode = FindNode(e, loopId);
    if (!loopNode) {
        e->lastError = "load_image_dir: loop node not found";
        rr.executedNodes = 0;
        rr.success = 0;
        return false;
    }

    auto down = DownstreamIdsFrom(e, loopId);
    auto order = TopoSort(e);
    int executed = 0;

    for (const auto& id : order) {
        if (down.count(id)) continue;
        const NodeDef* n = FindNode(e, id);
        if (!n) continue;
        std::string err;
        if (!ExecuteNode(e, *n, err)) {
            e->nodeErrors[id] = err;
            e->lastError = n->type + ": " + err;
            rr.executedNodes = executed;
            rr.success = 0;
            return false;
        }
        executed++;
    }

    std::string dir = TrimFlowToken(NodeParam(*loopNode, "directory", ""));
    if (dir.empty()) {
        e->lastError = "load_image_dir: directory empty";
        rr.executedNodes = executed;
        rr.success = 0;
        return false;
    }

    std::vector<std::string> paths;
    CollectImagePathsFromDir(dir, NodeParam(*loopNode, "extensions", ".bmp;.png;.jpg;.jpeg;.tif;.tiff"), paths);
    if (paths.empty()) {
        e->lastError = "load_image_dir: no matching images in " + dir;
        rr.executedNodes = executed;
        rr.success = 0;
        return false;
    }

    int okImages = 0;
    for (size_t ii = 0; ii < paths.size(); ii++) {
        std::string ferr;
        if (!FillLoadImageDirOutputsForPath(e, *loopNode, paths[ii], (int)paths.size(), ferr)) {
            std::fprintf(stderr, "[FlowNative] skip image %s: %s\n", paths[ii].c_str(), ferr.c_str());
            continue;
        }
        okImages++;
        for (const auto& id : order) {
            if (!down.count(id) || id == loopId) continue;
            const NodeDef* n = FindNode(e, id);
            if (!n) continue;
            std::string err;
            if (!ExecuteNode(e, *n, err)) {
                e->nodeErrors[id] = err;
                e->lastError = n->type + ": " + err;
                rr.executedNodes = executed;
                rr.success = 0;
                return false;
            }
            executed++;
        }
    }

    if (okImages <= 0) {
        e->lastError = "load_image_dir: could not decode any image";
        rr.executedNodes = executed;
        rr.success = 0;
        return false;
    }

    rr.executedNodes = rr.totalNodes;
    rr.success = 1;
    return true;
}

static void FinalizeFlowRunReport(NativeFlowEngineImpl* e, NativeFlowRunResult& rr) {
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
    if (n.type == "load_image_dir") {
        std::string mode = TrimFlowToken(NodeParam(n, "mode", "each"));
        for (char& c : mode) c = (char)std::tolower((unsigned char)c);
        if (mode == "each") {
            err = "internal: mode each is executed by FlowEngine_Run (not a single ExecuteNode step)";
            return false;
        }

        std::string dir = TrimFlowToken(NodeParam(n, "directory", ""));
        if (dir.empty()) { err = "load_image_dir: directory empty"; return false; }
        std::string extSpec = NodeParam(n, "extensions", ".bmp;.png;.jpg;.jpeg;.tif;.tiff");
        int idx = ToInt(NodeParam(n, "index", "0"), 0);

        std::vector<std::string> paths;
        CollectImagePathsFromDir(dir, extSpec, paths);

        if (paths.empty()) { err = "load_image_dir: no matching images in " + dir; return false; }
        if (idx < 0) idx = 0;
        if (idx >= (int)paths.size()) idx = (int)paths.size() - 1;

        cv::Mat img = cv::imread(paths[(size_t)idx], cv::IMREAD_UNCHANGED);
        if (img.empty()) { err = "load_image_dir: failed to read " + paths[(size_t)idx]; return false; }
        out["Image"] = MakeImage(img.channels() == 1 ? img : EnsureBgr(img));
        Value cnt;
        cnt.kind = Value::Kind::Int;
        cnt.i = (int)paths.size();
        out["Count"] = cnt;
        Value vp;
        vp.kind = Value::Kind::String;
        vp.str = paths[(size_t)idx];
        out["Path"] = vp;
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
    if (n.type == "dip_denoise") {
        err = "requires managed execution (PyTorch DIP)";
        return false;
    }
    if (n.type == "jit_sample") {
        err = "requires managed execution (JiT / JAX)";
        return false;
    }
    if (n.type == "swin_transformer") {
        err = "requires managed execution (PyTorch timm Swin)";
        return false;
    }
    if (n.type == "yolo_seg_infer") {
        err = "requires managed execution (Ultralytics YOLO-Seg)";
        return false;
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
        // 与托管 TrajectoryStepDetector.PreprocessAndFindContours 同源：Step_PreprocessAndFindContours
        cv::Mat grayWork = EnsureGray(vin.img).clone();
        cv::Mat binaryBright, morphed, mask, coloredMask;
        std::vector<std::vector<cv::Point>> outerContours;
        int blur = ToInt(NodeParam(n, "blurSize", "7"), 7);
        int morph = ToInt(NodeParam(n, "morphSize", "5"), 5);
        Step_PreprocessAndFindContours(&grayWork, &binaryBright, &morphed, &mask, &coloredMask, &outerContours,
            blur, morph, false, 0.0);
        if (binaryBright.empty()) { err = "binarize: empty output"; return false; }
        out["Out"] = MakeImage(binaryBright);
        return true;
    }
    if (n.type == "gray_range_binary") {
        Value vin = InputOf(e, n.id, "In");
        if (vin.kind != Value::Kind::Image) { err = "gray_range_binary: missing In"; return false; }
        std::string rangeMode = NodeParam(n, "rangeMode", "fixed");
        for (auto& c : rangeMode) c = (char)std::tolower((unsigned char)c);
        cv::Mat bin;
        if (rangeMode == "percentile") {
            double exL = ToDouble(NodeParam(n, "percentileExcludeLow", "10"), 10.0);
            double exH = ToDouble(NodeParam(n, "percentileExcludeHigh", "10"), 10.0);
            if (!GrayRangeBinaryPercentile(vin.img, exL, exH, bin, err))
                return false;
        } else {
            cv::Mat gray = EnsureGray(vin.img);
            int lo = ToInt(NodeParam(n, "grayLow", "5"), 5);
            int hi = ToInt(NodeParam(n, "grayHigh", "50"), 50);
            cv::inRange(gray, lo, hi, bin);
        }
        out["Out"] = MakeImage(bin);
        return true;
    }
    if (n.type == "gray_erode_rect") {
        Value vin = InputOf(e, n.id, "In");
        if (vin.kind != Value::Kind::Image) { err = "gray_erode_rect: missing In"; return false; }
        int kw = ToInt(NodeParam(n, "kernelW", "5"), 5);
        int kh = ToInt(NodeParam(n, "kernelH", "5"), 5);
        int iterations = std::max(1, ToInt(NodeParam(n, "iterations", "1"), 1));
        if ((kw & 1) == 0) kw++;
        if ((kh & 1) == 0) kh++;
        kw = std::max(1, kw);
        kh = std::max(1, kh);
        cv::Mat gray = EnsureGray(vin.img).clone();
        GrayMorphRectGrayIter(gray, false, kw, kh, iterations);
        out["Out"] = MakeImage(gray);
        return true;
    }
    if (n.type == "gray_dilate_rect") {
        Value vin = InputOf(e, n.id, "In");
        if (vin.kind != Value::Kind::Image) { err = "gray_dilate_rect: missing In"; return false; }
        int kw = ToInt(NodeParam(n, "kernelW", "5"), 5);
        int kh = ToInt(NodeParam(n, "kernelH", "5"), 5);
        int iterations = std::max(1, ToInt(NodeParam(n, "iterations", "1"), 1));
        if ((kw & 1) == 0) kw++;
        if ((kh & 1) == 0) kh++;
        kw = std::max(1, kw);
        kh = std::max(1, kh);
        cv::Mat gray = EnsureGray(vin.img).clone();
        GrayMorphRectGrayIter(gray, true, kw, kh, iterations);
        out["Out"] = MakeImage(gray);
        return true;
    }
    if (n.type == "binary_merge") {
        Value va = InputOf(e, n.id, "InA");
        Value vb = InputOf(e, n.id, "InB");
        if (va.kind != Value::Kind::Image || vb.kind != Value::Kind::Image) {
            err = "binary_merge: missing InA or InB";
            return false;
        }
        cv::Mat a = EnsureGray(va.img);
        cv::Mat b = EnsureGray(vb.img);
        if (a.size() != b.size()) {
            err = "binary_merge: image size mismatch";
            return false;
        }
        std::string mode = NodeParam(n, "mergeMode", "or");
        for (auto& c : mode)
            c = (char)std::tolower((unsigned char)c);
        int fgTh = ToInt(NodeParam(n, "foregroundThreshold", "0"), 0);
        cv::Mat ba, bb;
        if (fgTh < 0) {
            ba = a.clone();
            bb = b.clone();
        } else {
            cv::threshold(a, ba, fgTh, 255, cv::THRESH_BINARY);
            cv::threshold(b, bb, fgTh, 255, cv::THRESH_BINARY);
        }
        cv::Mat dst;
        if (mode == "and")
            cv::bitwise_and(ba, bb, dst);
        else if (mode == "xor")
            cv::bitwise_xor(ba, bb, dst);
        else
            cv::bitwise_or(ba, bb, dst);
        out["Out"] = MakeImage(dst);
        return true;
    }
    if (n.type == "binary_morph_rect") {
        Value vin = InputOf(e, n.id, "In");
        if (vin.kind != Value::Kind::Image) { err = "binary_morph_rect: missing In"; return false; }
        cv::Mat g = EnsureGray(vin.img);
        int fgTh = ToInt(NodeParam(n, "foregroundThreshold", "0"), 0);
        cv::Mat bin;
        if (fgTh < 0)
            cv::threshold(g, bin, 1, 255, cv::THRESH_BINARY);
        else
            cv::threshold(g, bin, fgTh, 255, cv::THRESH_BINARY);
        std::string orient = NodeParam(n, "orientation", "horizontal_strips");
        for (auto& c : orient) c = (char)std::tolower((unsigned char)c);
        int kw = ToInt(NodeParam(n, "kernelW", "0"), 0);
        int kh = ToInt(NodeParam(n, "kernelH", "0"), 0);
        if (kw <= 0 || kh <= 0) {
            if (orient.find("vertical") != std::string::npos) { kw = 5; kh = 31; }
            else { kw = 31; kh = 5; }
        }
        if ((kw & 1) == 0) kw++;
        if ((kh & 1) == 0) kh++;
        kw = std::max(1, kw);
        kh = std::max(1, kh);
        cv::Mat ker = cv::getStructuringElement(cv::MORPH_RECT, cv::Size(kw, kh));
        std::string mop = NodeParam(n, "op", "open");
        for (auto& c : mop) c = (char)std::tolower((unsigned char)c);
        int iterations = std::max(1, ToInt(NodeParam(n, "iterations", "1"), 1));
        cv::Mat outm = bin.clone();
        for (int i = 0; i < iterations; ++i) {
            if (mop == "erode") cv::erode(outm, outm, ker);
            else if (mop == "dilate") cv::dilate(outm, outm, ker);
            else if (mop == "close") cv::morphologyEx(outm, outm, cv::MORPH_CLOSE, ker);
            else cv::morphologyEx(outm, outm, cv::MORPH_OPEN, ker);
        }
        int minComp = ToInt(NodeParam(n, "minComponentPixels", "0"), 0);
        if (minComp > 1) {
            cv::Mat labels, stats, cent;
            int nlab = cv::connectedComponentsWithStats(outm, labels, stats, cent, 8, CV_32S);
            cv::Mat cleaned = cv::Mat::zeros(outm.size(), CV_8UC1);
            for (int li = 1; li < nlab; ++li) {
                int area = stats.at<int>(li, cv::CC_STAT_AREA);
                if (area >= minComp)
                    cleaned.setTo(255, labels == li);
            }
            outm = cleaned;
        }
        out["Out"] = MakeImage(outm);
        return true;
    }
    if (n.type == "gray_blend_ratio") {
        Value va = InputOf(e, n.id, "InA");
        Value vb = InputOf(e, n.id, "InB");
        if (va.kind != Value::Kind::Image || vb.kind != Value::Kind::Image) {
            err = "gray_blend_ratio: missing InA or InB";
            return false;
        }
        cv::Mat a = EnsureGray(va.img);
        cv::Mat b = EnsureGray(vb.img);
        if (a.size() != b.size()) {
            err = "gray_blend_ratio: image size mismatch";
            return false;
        }
        std::string bm = NodeParam(n, "grayBlendMode", "weighted");
        for (auto& c : bm)
            c = (char)std::tolower((unsigned char)c);
        cv::Mat dst;
        if (bm == "add" || bm == "sum")
            cv::add(a, b, dst);
        else if (bm == "subtract" || bm == "sub")
            cv::subtract(a, b, dst);
        else {
            double r = ToDouble(NodeParam(n, "ratioA", "0.5"), 0.5);
            r = std::max(0.0, std::min(1.0, r));
            cv::addWeighted(a, r, b, 1.0 - r, 0.0, dst, CV_8U);
        }
        out["Out"] = MakeImage(dst);
        return true;
    }
    if (n.type == "find_contours") {
        Value vin = InputOf(e, n.id, "In");
        if (vin.kind != Value::Kind::Image) { err = "find_contours: missing In"; return false; }
        // 与托管 SetDarkBinary + FindAndSortDarkContoursEx 同源
        cv::Mat darkBin = EnsureGray(vin.img).clone();
        int w = darkBin.cols, h = darkBin.rows;
        std::vector<std::pair<double, int>> sortedBars;
        std::vector<std::vector<cv::Point>> darkContours;
        std::string minAreaStr = NodeParam(n, "minContourArea", "");
        double minArea = -1.0;
        if (!minAreaStr.empty()) {
            try {
                minArea = std::stod(minAreaStr);
            } catch (...) {
                minArea = -1.0;
            }
        }
        Step_FindAndSortDarkContours(&darkBin, w, h, &sortedBars, &darkContours, minArea);
        std::vector<std::vector<cv::Point>> ordered;
        ordered.reserve(sortedBars.size());
        for (const auto& sb : sortedBars) {
            int ci = sb.second;
            if (ci >= 0 && ci < (int)darkContours.size())
                ordered.push_back(darkContours[ci]);
        }
        cv::Mat vis = darkBin.clone();
        for (size_t i = 0; i < sortedBars.size(); ++i) {
            int ci = sortedBars[i].second;
            if (ci >= 0 && ci < (int)darkContours.size())
                cv::drawContours(vis, darkContours, ci, cv::Scalar(128), 2);
        }
        Value c = MakeContours(ordered);
        out["Contours"] = c;
        out["Out"] = MakeImage(vis);
        Value cnt; cnt.kind = Value::Kind::Int; cnt.i = (int)ordered.size();
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
        if (idx < 0) {
            double bestA = -1.0;
            int bestI = 0;
            for (size_t i = 0; i < contours.size(); ++i) {
                double a = cv::contourArea(contours[i]);
                if (a > bestA) {
                    bestA = a;
                    bestI = (int)i;
                }
            }
            idx = bestI;
        } else if (idx >= (int)contours.size()) {
            err = "create_mask: contourIdx out of range";
            return false;
        }
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
        // 与托管 TrajectoryStepDetector.MorphologyCleanup 同源：Step_MorphologyCleanup
        cv::Mat dark = EnsureGray(in.img).clone();
        int k = ToInt(NodeParam(n, "kernelSize", "5"), 5);
        int bk = ToInt(NodeParam(n, "blurKsize", "9"), 9);
        double sigma = ToDouble(NodeParam(n, "blurSigma", "2.0"), 2.0);
        Step_MorphologyCleanup(&dark, k, bk, sigma);
        out["Out"] = MakeImage(dark);
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
        double minAsp = ToDouble(NodeParam(n, "minAspect", "0"), 0);
        double maxAsp = ToDouble(NodeParam(n, "maxAspect", "1e12"), 1e12);
        double minCirc = ToDouble(NodeParam(n, "minCircularity", "0"), 0);
        double maxCirc = ToDouble(NodeParam(n, "maxCircularity", "1"), 1);
        int target = ToInt(NodeParam(n, "targetCount", "16"), 16);
        std::string sortCy = NodeParam(n, "sortByCentroidY", "false");
        for (auto& c : sortCy) c = (char)std::tolower((unsigned char)c);
        bool sortCentroidY = (sortCy == "true" || sortCy == "1" || sortCy == "yes");
        struct Cand {
            std::vector<cv::Point> pts;
            double area;
            double cy;
        };
        std::vector<Cand> cand;
        for (const auto& c : contours) {
            double a = cv::contourArea(c);
            if (a < minArea || a > maxArea) continue;
            cv::Rect r = cv::boundingRect(c);
            double rw = std::max(1, r.width), rh = std::max(1, r.height);
            double aspect = rw / rh;
            if (aspect < minAsp || aspect > maxAsp) continue;
            double peri = cv::arcLength(c, true);
            double circ = peri <= 1e-6 ? 0.0 : (4.0 * CV_PI * a) / (peri * peri);
            if (minCirc > 0 && circ < minCirc) continue;
            if (maxCirc < 1.0 && circ > maxCirc) continue;
            Cand x;
            x.pts = c;
            x.area = a;
            x.cy = r.y + 0.5 * r.height;
            cand.push_back(std::move(x));
        }
        std::sort(cand.begin(), cand.end(), [](const Cand& a, const Cand& b) { return a.area > b.area; });
        if ((int)cand.size() > target) cand.resize(target);
        if (sortCentroidY)
            std::sort(cand.begin(), cand.end(), [](const Cand& a, const Cand& b) { return a.cy < b.cy; });
        std::vector<std::vector<cv::Point>> kept;
        kept.reserve(cand.size());
        for (auto& x : cand) kept.push_back(std::move(x.pts));
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
        // Match FlowPage: GroupBy($"{Round(X,3)}_{Round(Y,3)}").Select(g => g.First())
        std::unordered_set<std::string> seen;
        seen.reserve(v.points.size() * 2);
        Value ov; ov.kind = Value::Kind::Points;
        for (size_t i = 0; i < v.points.size(); ++i) {
            double rx = std::round(v.points[i].x * 1000.0) / 1000.0;
            double ry = std::round(v.points[i].y * 1000.0) / 1000.0;
            std::ostringstream ks;
            ks << std::fixed << std::setprecision(3) << rx << '_' << ry;
            std::string key = ks.str();
            if (!seen.insert(key).second) continue;
            ov.points.push_back(v.points[i]);
            if (i < v.barIds.size()) ov.barIds.push_back(v.barIds[i]);
        }
        out["Out"] = ov;
        return true;
    }
    if (n.type == "fit_shape") {
        Value v = InputOf(e, n.id, "In");
        Value vb = InputOf(e, n.id, "BarIds");
        if (v.kind != Value::Kind::Points) { err = "fit_shape: missing In"; return false; }
        const auto& inPts = v.points;
        if (inPts.size() < 3) {
            out["Out"] = v;
            Value emptyBid; emptyBid.kind = Value::Kind::Points;
            out["OutBarIds"] = emptyBid;
            return true;
        }

        std::string mode = NodeParam(n, "mode", "hybrid");
        for (auto& c : mode) c = (char)std::tolower((unsigned char)c);

        int windowRadius = ToInt(NodeParam(n, "windowRadius", "1"), 1);
        if (windowRadius < 0) windowRadius = 0;

        double epsilon = ToDouble(NodeParam(n, "epsilon", "2.0"), 2.0);
        if (epsilon < 0) epsilon = 0;

        double splitGapFactor = ToDouble(NodeParam(n, "splitGapFactor", "3.0"), 3.0);
        if (splitGapFactor < 1.2) splitGapFactor = 1.2;

        int minRegionPoints = ToInt(NodeParam(n, "minRegionPoints", "16"), 16);
        if (minRegionPoints < 3) minRegionPoints = 3;

        std::vector<int> inputBarIds = !vb.barIds.empty() ? vb.barIds : v.barIds;
        if (inputBarIds.size() != inPts.size())
            inputBarIds.clear();

        auto FitOneRegion = [&](const std::vector<Point2D>& region) -> std::vector<Point2D> {
            if (region.size() < 3) return region;
            int wr = windowRadius <= 0 ? 1 : windowRadius;
            double epsUse = epsilon <= 0 ? 1.0 : epsilon;
            if (mode == "moving_avg")
                return FlowSmoothPointsClosed(region, wr);
            if (mode == "simplify") {
                auto simplified = FlowSimplifyClosedPolyline(region, epsUse);
                return FlowResampleClosedPolyline(simplified, (int)region.size());
            }
            auto simplified = FlowSimplifyClosedPolyline(region, epsUse);
            auto resampled = FlowResampleClosedPolyline(simplified, (int)region.size());
            return FlowSmoothPointsClosed(resampled, wr);
        };

        std::vector<Point2D> fitPts;
        std::vector<int> outBarIds;

        auto barRegions = FlowSplitRegionsByBarIds(inPts, inputBarIds, minRegionPoints);
        if (!barRegions.empty()) {
            for (auto& pr : barRegions) {
                auto fitted = FitOneRegion(pr.first);
                fitPts.insert(fitPts.end(), fitted.begin(), fitted.end());
                if (fitted.size() == pr.second.size())
                    outBarIds.insert(outBarIds.end(), pr.second.begin(), pr.second.end());
                else {
                    int bid = pr.second.empty() ? -1 : pr.second[0];
                    outBarIds.insert(outBarIds.end(), fitted.size(), (size_t)bid);
                }
            }
        } else {
            auto regions = FlowSplitIntoClosedRegions(inPts, splitGapFactor, minRegionPoints);
            std::vector<Point2D> merged;
            merged.reserve(inPts.size());
            for (auto& region : regions)
                merged.insert(merged.end(), region.begin(), region.end());
            if (merged.size() == inPts.size())
                fitPts = std::move(merged);
            else
                fitPts = FitOneRegion(inPts);
            if (inputBarIds.size() == fitPts.size())
                outBarIds = inputBarIds;
        }

        Value ov; ov.kind = Value::Kind::Points; ov.points = std::move(fitPts);
        Value obid; obid.kind = Value::Kind::Points; obid.barIds = std::move(outBarIds);
        out["Out"] = ov;
        out["OutBarIds"] = obid;
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
        Value pts = InputOf(e, n.id, "Pixel");
        if (pts.kind != Value::Kind::Points)
            pts = InputOf(e, n.id, "Points");
        Value tr = InputOf(e, n.id, "Transform");
        if (pts.kind != Value::Kind::Points || tr.kind != Value::Kind::Transform) {
            err = "img_to_world: missing Pixel/Points or Transform";
            return false;
        }
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
    if (n.type == "polyline_simplify_dp") {
        Value ptsIn = InputOf(e, n.id, "In");
        // err 勿再加算子名前缀：调用方会写入 lastError = type + ": " + err
        if (ptsIn.kind != Value::Kind::Points) { err = "missing In (need Points)"; return false; }
        if (ptsIn.points.empty()) {
            Value vout;
            vout.kind = Value::Kind::Points;
            vout.points.clear();
            out["Out"] = vout;
            return true;
        }
        double eps = ToDouble(NodeParam(n, "epsilon", "2.0"), 2.0);
        if (eps <= 0) eps = 1e-6;
        std::string closedStr = NodeParam(n, "closed", "true");
        for (auto& c : closedStr) c = (char)std::tolower((unsigned char)c);
        bool closed = !(closedStr == "0" || closedStr == "false");
        std::vector<Point2D> simplified;
        if (ptsIn.points.size() <= 2)
            simplified = ptsIn.points;
        else if (closed)
            simplified = FlowSimplifyClosedPolyline(ptsIn.points, eps);
        else
            simplified = FlowSimplifyOpenPolyline(ptsIn.points, eps);
        Value vout;
        vout.kind = Value::Kind::Points;
        vout.points = std::move(simplified);
        out["Out"] = vout;
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
        Value in = InputOf(e, n.id, "Image");
        if (in.kind != Value::Kind::Image) in = InputOf(e, n.id, "In");
        if (in.kind != Value::Kind::Image) { err = "detect_circles: missing Image/In"; return false; }
        cv::Mat gray = EnsureGray(in.img);
        std::vector<cv::Vec3f> circles;
        cv::HoughCircles(gray, circles, cv::HOUGH_GRADIENT, 1.0, 20.0, 120, 20, 3, 60);
        Value pts; pts.kind = Value::Kind::Points;
        for (auto& c : circles) pts.points.push_back(Point2D{ c[0], c[1] });
        out["Points"] = pts;
        Value cnt; cnt.kind = Value::Kind::Int; cnt.i = (int)circles.size(); out["Count"] = cnt;
        return true;
    }
    if (n.type == "hough_circles") {
        Value in = InputOf(e, n.id, "Image");
        if (in.kind != Value::Kind::Image) { err = "hough_circles: missing Image"; return false; }
        int blurK = ToInt(NodeParam(n, "blurKsize", "9"), 9);
        double hcDp = ToDouble(NodeParam(n, "hcDp", "1.2"), 1.2);
        double hcMinDist = ToDouble(NodeParam(n, "hcMinDist", "40"), 40.0);
        double hcP1 = ToDouble(NodeParam(n, "hcParam1", "100"), 100.0);
        double hcP2 = ToDouble(NodeParam(n, "hcParam2", "30"), 30.0);
        int hcMinR = ToInt(NodeParam(n, "hcMinRadius", "5"), 5);
        int hcMaxR = ToInt(NodeParam(n, "hcMaxRadius", "200"), 200);
        std::vector<Point2D> circ;
        cv::Mat dst;
        std::string cj;
        HoughCirclesOnMat(in.img, dst, circ, blurK, hcDp, hcMinDist, hcP1, hcP2, hcMinR, hcMaxR, &cj);
        out["Out"] = MakeImage(dst);
        const int nCirc = (int)circ.size();
        Value vp; vp.kind = Value::Kind::Points; vp.points = std::move(circ); out["CirclePoints"] = vp;
        Value vc; vc.kind = Value::Kind::Int; vc.i = nCirc; out["CircleCount"] = vc;
        Value vj; vj.kind = Value::Kind::String; vj.str = std::move(cj); out["CirclesJson"] = vj;
        return true;
    }
    if (n.type == "hough_lines") {
        Value in = InputOf(e, n.id, "Edge");
        if (in.kind != Value::Kind::Image) in = InputOf(e, n.id, "Image");
        if (in.kind != Value::Kind::Image) { err = "hough_lines: missing Edge (or legacy Image)"; return false; }
        double hlRho = ToDouble(NodeParam(n, "hlRho", "1"), 1.0);
        double hlThetaDeg = ToDouble(NodeParam(n, "hlThetaDeg", "1"), 1.0);
        int hlTh = ToInt(NodeParam(n, "hlThreshold", "50"), 50);
        double hlMinLen = ToDouble(NodeParam(n, "hlMinLineLength", "40"), 40.0);
        double hlMaxGap = ToDouble(NodeParam(n, "hlMaxLineGap", "15"), 15.0);
        int maxLines = ToInt(NodeParam(n, "maxLinesOut", "400"), 400);
        int covHalfW = ToInt(NodeParam(n, "hlCoverageHalfWidthPx", "0"), 0);
        if (covHalfW < 0) covHalfW = 0;
        std::string lj;
        cv::Mat dst;
        int nseg = 0;
        HoughLinesOnMat(in.img, dst, lj, &nseg, hlRho, hlThetaDeg, hlTh, hlMinLen, hlMaxGap, maxLines, covHalfW);
        out["Out"] = MakeImage(dst);
        Value vs; vs.kind = Value::Kind::String; vs.str = std::move(lj); out["LinesJson"] = vs;
        Value vn; vn.kind = Value::Kind::Int; vn.i = nseg; out["LineCount"] = vn;
        return true;
    }
    if (n.type == "lines_nms") {
        Value vin = InputOf(e, n.id, "LinesJson");
        if (vin.kind != Value::Kind::String) { err = "lines_nms: missing LinesJson"; return false; }
        double angleTol = ToDouble(NodeParam(n, "angleTolDeg", "5"), 5.0);
        double rhoTol = ToDouble(NodeParam(n, "rhoTolPx", "10"), 10.0);
        std::vector<cv::Vec4i> segs;
        if (!vin.str.empty())
            ParseHoughLinesJsonUtf8(vin.str.c_str(), segs);
        std::vector<cv::Vec4i> fi;
        LinesJsonNmsBucket(segs, angleTol, rhoTol, fi);
        Value vs; vs.kind = Value::Kind::String; vs.str = FormatLinesJsonVec4(fi); out["LinesJson"] = vs;
        Value vn; vn.kind = Value::Kind::Int; vn.i = (int)fi.size(); out["LineCount"] = vn;
        return true;
    }
    if (n.type == "lines_threshold") {
        Value vin = InputOf(e, n.id, "LinesJson");
        if (vin.kind != Value::Kind::String) { err = "lines_threshold: missing LinesJson"; return false; }
        double minLen = ToDouble(NodeParam(n, "minLengthPx", "0"), 0.0);
        double maxLen = ToDouble(NodeParam(n, "maxLengthPx", "0"), 0.0);
        std::vector<cv::Vec4i> segs;
        if (!vin.str.empty())
            ParseHoughLinesJsonUtf8(vin.str.c_str(), segs);
        std::vector<cv::Vec4i> fi;
        LinesJsonThresholdLen(segs, minLen, maxLen, fi);
        Value vs; vs.kind = Value::Kind::String; vs.str = FormatLinesJsonVec4(fi); out["LinesJson"] = vs;
        Value vn; vn.kind = Value::Kind::Int; vn.i = (int)fi.size(); out["LineCount"] = vn;
        return true;
    }
    if (n.type == "hough_runway") {
        Value in = InputOf(e, n.id, "Image");
        if (in.kind != Value::Kind::Image) { err = "hough_runway: missing Image"; return false; }
        int blurK = ToInt(NodeParam(n, "blurKsize", "9"), 9);
        double c1 = ToDouble(NodeParam(n, "cannyTh1", "50"), 50.0);
        double c2 = ToDouble(NodeParam(n, "cannyTh2", "150"), 150.0);
        double hlRho = ToDouble(NodeParam(n, "hlRho", "1"), 1.0);
        double hlThetaDeg = ToDouble(NodeParam(n, "hlThetaDeg", "1"), 1.0);
        int hlTh = ToInt(NodeParam(n, "hlThreshold", "50"), 50);
        double hlMinLen = ToDouble(NodeParam(n, "hlMinLineLength", "40"), 40.0);
        double hlMaxGap = ToDouble(NodeParam(n, "hlMaxLineGap", "15"), 15.0);
        int maxLines = ToInt(NodeParam(n, "maxLinesOut", "400"), 400);
        double rwAng = ToDouble(NodeParam(n, "runwayAngleTolDeg", "10"), 10.0);
        int rwRho = ToInt(NodeParam(n, "runwayRhoBinPx", "25"), 25);
        int rwStrips = ToInt(NodeParam(n, "runwayStripCount", "2"), 2);
        int maxRw = ToInt(NodeParam(n, "maxRunwayLinesOut", "120"), 120);
        std::string rwShape = NodeParam(n, "runwayShape", "parallel");
        int shapeMode = 0;
        if (rwShape == "stadium")
            shapeMode = 1;
        double hcDp = ToDouble(NodeParam(n, "hcDp", "1.2"), 1.2);
        double hcMinDist = ToDouble(NodeParam(n, "hcMinDist", "40"), 40.0);
        double hcP1 = ToDouble(NodeParam(n, "hcParam1", "100"), 100.0);
        double hcP2 = ToDouble(NodeParam(n, "hcParam2", "30"), 30.0);
        int hcMinR = ToInt(NodeParam(n, "hcMinRadius", "5"), 5);
        int hcMaxR = ToInt(NodeParam(n, "hcMaxRadius", "200"), 200);
        Value vLinesIn = InputOf(e, n.id, "LinesJson");
        Value vCircIn = InputOf(e, n.id, "CirclesJson");
        std::vector<cv::Vec4i> optLinesParsed;
        std::vector<cv::Vec3f> optCirclesParsed;
        const std::vector<cv::Vec4i>* plines = nullptr;
        const std::vector<cv::Vec3f>* pcirc = nullptr;
        if (vLinesIn.kind == Value::Kind::String && !vLinesIn.str.empty()) {
            if (ParseHoughLinesJsonUtf8(vLinesIn.str.c_str(), optLinesParsed))
                plines = &optLinesParsed;
        }
        if (vCircIn.kind == Value::Kind::String && !vCircIn.str.empty()) {
            if (ParseHoughCirclesJsonUtf8(vCircIn.str.c_str(), optCirclesParsed))
                pcirc = &optCirclesParsed;
        }
        std::string rj;
        cv::Mat dst;
        int rwSegCount = 0;
        HoughRunwayOnMat(in.img, dst, rj, &rwSegCount, blurK, c1, c2, hlRho, hlThetaDeg, hlTh, hlMinLen, hlMaxGap, maxLines,
            rwAng, rwRho, rwStrips, maxRw, shapeMode, hcDp, hcMinDist, hcP1, hcP2, hcMinR, hcMaxR,
            plines, pcirc);
        out["Out"] = MakeImage(dst);
        Value vrj; vrj.kind = Value::Kind::String; vrj.str = std::move(rj); out["RunwayLinesJson"] = vrj;
        Value vrc; vrc.kind = Value::Kind::Int; vrc.i = rwSegCount; out["RunwayLineCount"] = vrc;
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
    if (n.type == "load_calibration_result") {
        std::string path = NodeParam(n, "filePath", "");
        if (path.empty()) {
            err = "load_calibration_result: filePath empty";
            return false;
        }
        std::string rp = path;
        if (!FlowPathIsAbsolute(path) && !e->flowRootDir.empty())
            rp = FlowJoinPath(e->flowRootDir, path);
        std::string jsonBuf;
        if (!ReadCalibrationJsonFileForOpenCv(rp, jsonBuf, err)) {
            err = "load_calibration_result: " + err;
            return false;
        }
        std::string calJsonField;
        AffineTransform t{};
        if (!ParseCalibrationResultJsonMinimal(jsonBuf, t, &calJsonField, err)) {
            err = "load_calibration_result: " + err;
            return false;
        }
        Value tv;
        tv.kind = Value::Kind::Transform;
        tv.trans = t;
        out["Transform"] = tv;
        if (!calJsonField.empty()) {
            Value vs;
            vs.kind = Value::Kind::String;
            vs.str = std::move(calJsonField);
            out["CalibrationJson"] = vs;
        }
        return true;
    }
    if (n.type == "save_image") {
        Value img = InputOf(e, n.id, "Image");
        if (img.kind != Value::Kind::Image) { err = "save_image: missing Image"; return false; }
        std::string path = NodeParam(n, "filePath", "flow_output.bmp");
        std::string wp = path;
        if (!path.empty() && !FlowPathIsAbsolute(path) && !e->flowRootDir.empty())
            wp = FlowJoinPath(e->flowRootDir, path);
        if (!cv::imwrite(wp, img.img)) { err = "save_image: failed " + wp; return false; }
        out["Out"] = img;
        return true;
    }
    if (n.type == "save_text") {
        Value v = InputOf(e, n.id, "Text");
        if (v.kind != Value::Kind::String) { err = "save_text: missing Text"; return false; }
        std::string path = NodeParam(n, "filePath", "flow_output.txt");
        std::string wp = path;
        if (!path.empty() && !FlowPathIsAbsolute(path) && !e->flowRootDir.empty())
            wp = FlowJoinPath(e->flowRootDir, path);
        std::ofstream ofs(wp, std::ios::binary | std::ios::trunc);
        if (!ofs) { err = "save_text: cannot open " + wp; return false; }
        ofs.write(v.str.data(), (std::streamsize)v.str.size());
        if (!ofs.good()) { err = "save_text: write failed " + wp; return false; }
        Value ov; ov.kind = Value::Kind::String; ov.str = v.str; out["Out"] = ov;
        return true;
    }

    err = "unsupported operator: " + n.type;
    return false;
}

static bool LoadFlowFromCvFileStorage(NativeFlowEngineImpl* e, cv::FileStorage& fs, std::string& err) {
    if (!ReadFlowGraph(fs, e->nodes, e->conns, err))
        return false;
    return ExpandAllCompositeNodes(e, err);
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
    e->flowRootDir.clear();
    cv::FileStorage fs(flowFilePath, cv::FileStorage::READ | cv::FileStorage::FORMAT_JSON);
    if (!fs.isOpened()) {
        e->lastError = std::string("cannot open flow file: ") + flowFilePath;
        return -1;
    }
    std::string dir = FlowDirName(std::string(flowFilePath));
    if (!dir.empty())
        e->flowRootDir = FlowCanonicalPathA(dir);
    std::string err;
    bool ok = LoadFlowFromCvFileStorage(e, fs, err);
    fs.release();
    if (!ok) { e->lastError = err; return -1; }
    return 0;
}

int FlowEngine_LoadFromJson(NativeFlowEngineHandle handle, const char* flowJsonText, const char* flowRootDirectoryOrNull) {
    if (!handle || !flowJsonText) return -1;
    auto* e = static_cast<NativeFlowEngineImpl*>(handle);
    e->lastError.clear();
    e->flowRootDir.clear();
    if (flowRootDirectoryOrNull && flowRootDirectoryOrNull[0])
        e->flowRootDir = FlowCanonicalPathA(std::string(flowRootDirectoryOrNull));
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

    std::string dirEachId;
    std::string resolveErr;
    if (!ResolveLoadImageDirEachLoopId(e, dirEachId, resolveErr)) {
        e->lastError = resolveErr;
        rr.success = 0;
        rr.executedNodes = 0;
        FinalizeFlowRunReport(e, rr);
        return rr;
    }

    if (!dirEachId.empty()) {
        if (HasCameraLoopPerFrame(e)) {
            e->lastError = "load_image_dir(each) cannot be used with camera_loop(per_frame)";
            rr.success = 0;
            rr.executedNodes = 0;
            FinalizeFlowRunReport(e, rr);
            return rr;
        }
        RunLoadImageDirEach(e, dirEachId, rr);
        FinalizeFlowRunReport(e, rr);
        return rr;
    }

    auto order = TopoSort(e);
    for (const auto& id : order) {
        const NodeDef* n = FindNode(e, id);
        if (!n) continue;
        std::string err;
        auto t0 = std::chrono::steady_clock::now();
        bool ok = ExecuteNode(e, *n, err);
        auto t1 = std::chrono::steady_clock::now();
        double ms = std::chrono::duration<double, std::milli>(t1 - t0).count();
        std::fprintf(stderr, "[FlowNative][Timing] %s id=%s %.2f ms%s\n", n->type.c_str(), n->id.c_str(), ms,
            ok ? "" : " (failed)");
        if (!ok) {
            e->nodeErrors[id] = err;
            e->lastError = n->type + ": " + err;
            break;
        }
        rr.executedNodes++;
    }
    rr.success = rr.executedNodes == rr.totalNodes ? 1 : 0;

    FinalizeFlowRunReport(e, rr);
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
