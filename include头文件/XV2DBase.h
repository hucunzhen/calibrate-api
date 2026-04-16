#ifndef XV2DBASE_H
#define XV2DBASE_H
#include "XVBase.h"
namespace XVL
{
	//像素
	struct XVPixel
	{
		float ch1;//第1通道值
		float ch2;//第2通道值
		float ch3;//第3通道值
		float ch4;//第4通道值
	};

	//线段信息
	struct XVAxisInfo {
		float length;//轴长
		float angle;//角度(°)
		XVSegment2D segment;//轴
		XVPoint2D center;//轴中点
	};

	//镜像类型
	enum MirrorDirection
	{
		Horizontal, //水平
		Vertical,  //垂直
		Both       //水平垂直
	};

	//旋转角度
	enum RotateAngle
	{
		Ninety,//顺时针90度
		OneHundredAndEighty,//顺时针180度
		TwoHundredAndSeventy//顺时针270度
	};

	enum CopyAndFillType
	{
		Copy,//拷贝
		Fill//填充
	};

	//尺寸模式
	enum SizeMode
	{
		Preserve,//保留，默认维持原图宽度和高度
		Fit,//自适应，根据roi区域重新计算新的图像宽度和高度
	};

	//分割模式
	enum XVSegmentMode
	{
		segments,//线段
		segmentsArcs //线段圆弧
	};

	//路径类型
	enum XVPathFilter
	{
		All, //所有路径
		ClosedOnly, //仅闭路径
		OpenOnly //仅开路径
	};

	//路径特征
	enum XVPathFeature
	{
		Length, //长度
		MassCenterX, //质心横坐标
		MassCenterY, //质心纵坐标
		Area, //面积
		Convexity, //凸度
		Circularity, //圆度
		Rectangularity, //矩形度
		Elongation //延伸率
	};

	//点位置
	enum  PointLocation
	{
		inShape,     //形状内部
		onShape,     //形状上
		outShape     //形状外部
	};

	//多边形位置
	enum PolygonLocation
	{
		inPolygon,         //形状内部
		crossoverPolygon,  //与形状相交
		outPolygon         //形状外部
	};

	//锚点类型
	enum Anchor
	{
		TopLeft,         //左上顶点
		TopCenter,       //上边中点
		TopRight,        //右上顶点
		MiddleLeft,      //左边中点
		MiddleCenter,    //矩形中心
		MiddleRight,     //右边中点
		BottomLeft,      //左下顶点 
		BottomCenter,    //下边中点
		BottomRight      //右下顶点
	};

	//拐角方向
	enum TurnDerection
	{
		Right, //仅右拐
		Left,  //仅左拐
		ALL    //两者
	};

	//路径距离类型
	enum PathDistanceMode
	{
		PointtoPoint,       //点到端点
		PointtoSegment      //点到线
	};

	//区域特征枚举
	enum XVClassifyFeature
	{
		XCF_Area,           //面积
		XCF_Circularity_boundingCircle,//似圆度_外接圆
		XCF_Circularity_radius,    //似圆度_半径
		XCF_Convexity,             //凸度
		XCF_Elogation,        //延伸率
		XCF_MassCenterX,     //质心x
		XCF_MassCenterY,     //质心y
		XCF_MajorAxis,       //长轴长
		XCF_MinorAxis,       //短轴长
		XCF_Orientation,     //方向
		XCF_Rectangularity,  //矩形度
		XCF_UpperLeftX,//外接框左上角X
		XCF_UpperLeftY//外接框左上角Y
	};

	//圆形度=区域面积/圆面积，此圆与区域有一个相同的特征，如下:
	enum XVCircularityMeasure
	{
		radius,//半径
		boundingCircle,//外接圆
		perimeter//周长
	};

	//矩形方向
	enum XVRectangleOrientation
	{
		XVO_Horizonal,//水平
		XVO_Vertical//竖直
	};

	//形态学运算类型
	enum XVMorphType
	{
		Dilate,//膨胀
		Erode,//腐蚀
		Open,//开操作
		Close,//闭操作
	};

	//形态学核类型
	enum XVMorphShape
	{
		Rect,//矩形
		Cross,//交叉形
		Ellipse//椭圆
	};

	//图像类型
	enum XVImageType
	{
		RGB,
		HSI,
		HSL,
		HSV
	};

	//圆类型
	enum BorderPosition
	{
		Internal,//内圆
		External,//外圆
		Centered//中心圆
	};

	//轮廓类型
	enum XVMode
	{
		XM_All,//所有轮廓
		XM_External,//外轮廓
		XM_Internal//内轮廓
	};

	//轮廓模式
	enum XVMethod {
		XM_Edge,//边缘模式
		XM_Center//中心模式
	};

	//图像运算类型
	enum XVImageComputationType
	{
		XVC_Add,//加，默认值
		XVC_Subtract,//减
		XVC_Difference,//差分
		XVC_And,//与
		XVC_Or,//或
		XVC_Xor,//异或
		XVC_Min,//最小值
		XVC_Max,//最大值
		XVC_Average//平均值
	};

	//图像四则运算类型
	enum XVImageFourOperationsType
	{
		XVF_ADD,//加法
		XVF_SUBTRACT,//减法
		XVF_DIVIDE,//除法
		XVF_MULTIPLY//乘法
	};

	//距离类型
	enum XVDistanceMeasuring
	{
		XVD_Euclid,   //欧氏距离
		XVD_Manhattan,//街区距离
		XVD_Chessboard//棋盘距离
	};

	//掩模尺寸
	enum XVMaskSize
	{
		XVThree,       //边长为3的正方形掩模
		XVFive,        //边长为5的正方形掩模
		XVPrecise      //精确计算
	};

	//图像几何变换类型
	enum XVTransformationType
	{
		Mirror,//镜像
		Shift,//平移
		Scale,//缩放
		Rotate,//旋转
		Affine//仿射
	};

	//插值法
	enum XVInterpolationMethod
	{
		NearestNeighbor,//最近邻
		Bilinear//双线性
	};

	//镜像类型
	enum XVMirrorType
	{
		VerticalMirror,//垂直镜像
		HorizonalMirror,//水平镜像
	};

	//尺寸模式
	enum XVSizeMode
	{
		XVS_Fit,     //自适应尺寸
		XVS_Preserve //保留原图尺寸
	};

	//缩放方式
	enum XVScaleMethod
	{
		Absolute,//绝对
		Relative//相对
	};

	//图像归一化类型
	enum XVImageNormalizationType
	{
		HistogramEqualization,//直方图均衡化
		HistogramNormalization,//直方图归一化
		AveStandardDeviationNormalization//均值标准差归一化
	};

	//平滑类型
	enum SmoothType
	{
		XVNGauss,//高斯
		XVNMean//均值
	};

	//坐标排序类型
	enum XVRobotFlangePoseSortFeature
	{
		RobotFlangePoseX,//根据法兰坐标X排序
		RobotFlangePoseY //根据法兰坐标Y排序
	};

	//离群点抑制方法
	enum XVMestimator
	{
		Huber,
		Tukey
	};

	//
	enum XVInterMethod
	{
		//Linear,   //线性插值(切线插值)
		Parabola, //抛物线插值(二次插值)
		//Cubic     //三次插值
	};

	//选择模式
	enum XVSelection
	{
		Best,
		First,
		Last
	};

	//边缘类型
	enum XVEdgeType
	{
		brightTodark, //白到黑
		darkTobright  //黑到白
	};

	//条带类型
	enum XVStripeType
	{
		DARK,  //黑
		BRIGHT  //白
	};

	//圆拟合方法
	enum XVFittingMethod
	{
		AlgebraicTaubin,//(代数法)
		GeometricMethod//(几何法)
	};

	//测量宽度方式
	enum XVMeasureObjectMethod
	{
		FittedSegmentDistance,//(两拟合线段距离)
		AverageDistance,//(平均距离)
		MedianDistance,//(中值距离)
		MinimumDistance,//(最小距离)
		MaximumDistance//(最大距离)
	};

	//缺陷极性
	enum XVDefectPolarity
	{
		TrackLeftDefect, //轨迹左侧缺陷
		TrackRightDefect, //轨迹右侧缺陷
		TrackBilateralDefect //轨迹两侧缺陷
	};

	//极坐标直线
	struct XVLinePolar
	{
		float rho;    //极坐标ρ
		float angle; //极坐标Θ
	};

	//清晰度评估方式
	enum ScoreMethod
	{
		ENTROY,//信息熵
		SOBEL,//Sobel梯度下降
		LAPLACIAN,//拉普拉斯梯度下降
		BRENNER,//Brenner梯度下降
		SMD2,//灰度差分乘积
		VOLLATH,//VOLLATH
		VARIANCE,//方差
		RF_CF,//空间频率
	};

	enum XVRidgeOperator
	{
		Minimum,//最小值法
		Mean,//均值法
	};
	
	enum BorderType 
	{
		Mirror_s,
		Copy_s,
		Fill_s
	};

	//图像处理方式
	enum EncryptionMethod
	{
		Encryption,//加密
		Decryption//解密
	};

	enum DownsampleFuction
	{
		sample_Mean,//均值采样
		sample_Max,//最大值采样
		sample_Min//最小值采样
	};

	struct XVEllipse2D
	{
		XVPoint2D center;                  //中心
		float angle;                       //角度(单位：°)，width边与x轴正方向的夹角
		float width;                      //宽
		float height;                     //高
	};
}
#endif